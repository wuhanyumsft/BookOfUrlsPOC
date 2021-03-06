﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Document.Hosting.RestClient;
using Microsoft.Document.Hosting.RestService.Contract;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Linq;

namespace BookOfUrlsPOCdotnetfull
{
    public class Program
    {
        private static string newDepotName = "MSDN.dotnet.ref";
        private static string newDepotBasePath = "dotnet/ref";
        static readonly Dictionary<string, int> CountDict = new Dictionary<string, int>();
        static readonly Dictionary<string, IList<GetDocumentResponse>> DocumentDict = new Dictionary<string, IList<GetDocumentResponse>>();
        static readonly CloudStorageAccount StorageAccount = CloudStorageAccount.Parse("");
        static readonly IDocumentHostingService Client = new DocumentHostingServiceClient(
                new Uri("https://op-dhs-sandbox-pub.azurewebsites.net"),
                "integration_test",
                "",
                TimeSpan.FromSeconds(10),
                null);
        static readonly IDocumentHostingService ProdClient = new DocumentHostingServiceClient(
                new Uri("https://opdhs-prod.microsoftonedoc.com"),
                "msdn_rendering",
                "",
                TimeSpan.FromSeconds(10),
                null);

        private static readonly string HardCodedPrefix = "core/api/";

        public static void Main(string[] args)
        {
            //CreateDepot();
            GetMergedRepo(Client, ProdClient);
            var tocString = GetMergedToc();
            ReplaceToc(tocString);
            GenerateDisambiguosPages();
            Console.ReadLine();
        }

        public static void GenerateDisambiguosPages()
        {
            IDictionary<string, string> mappingName = new Dictionary<string, string>
            {
                { "VS.core-docs", "DotNetCore" },
                { "MSDN.aspnetAPIDocs", "ASPDotNetCore" }
            };
            CancellationToken cancellationToken = new CancellationToken();
            HashSet<string> blackList = new HashSet<string> { "index", "toc.json", "_themes" };
            var duplicatedDcouments = DocumentDict.Where(e => !blackList.Contains(e.Key.Split('/').First()) && e.Value.Count > 1);
            foreach (var duplicatedDocument in duplicatedDcouments)
            {
                Console.WriteLine(duplicatedDocument.Key);
                var disabmbigousPage = GetDisambigousPage(duplicatedDocument.Key,
                    duplicatedDocument.Value.Select(
                        v => Tuple.Create<string, string>(v.DepotName, $"{v.AssetId}({mappingName[v.DepotName]})")).ToArray());
                var url = GetBlobUrlByCreatingOne(disabmbigousPage);
                var putDisambigousPageRequest = new PutDocumentRequest
                {
                    Metadata = new Dictionary<string, object>(),
                    ContentSourceUri = GetCorrectedCanonicalUrl(url, duplicatedDocument.Key)
                };
                foreach (var item in duplicatedDocument.Value)
                {
                    var newAssetId = $"{item.AssetId}({mappingName[item.DepotName]})";
                    var putDocumentRequest = new PutDocumentRequest
                    {
                        Metadata = item.Metadata,
                        ContentSourceUri = GetCorrectedCanonicalUrl(InsertDisambiguatedDisclaimer(item.ContentUri, item.AssetId), newAssetId)
                    };
                    Client.PutDocument(newDepotName, newAssetId, item.Locale,
                        item.ProductVersion, "master", putDocumentRequest, null, cancellationToken).Wait();
                    putDisambigousPageRequest.Metadata = item.Metadata;
                    Client.PutDocument(newDepotName, item.AssetId, item.Locale,
                        item.ProductVersion, "master", putDisambigousPageRequest, null, cancellationToken).Wait();
                }
            }
            Console.ReadKey();
        }

        private static string InsertDisambiguatedDisclaimer(string uri, string assetId)
        {
            HtmlDocument doc = new HtmlDocument();
            using (WebClient client = new WebClient())
            {
                string s = client.DownloadString(uri);
                doc.LoadHtml(s);
                var h1Node = doc.DocumentNode.SelectSingleNode("//div[@id='main']//h1");
                h1Node.ParentNode.InsertAfter(HtmlNode.CreateNode($"<div class='IMPORTANT alert'><h5>DISAMBIGUATED CONTENT</h5><p>The definition is also available in other framework. Click the <a href='{assetId}'>disambiguation page</a>.</div>"), h1Node);
            }
            
            return GetBlobUrlByCreatingOne(doc.DocumentNode.OuterHtml);
        }

        private static string GetBlobUrlByCreatingOne(string content)
        {
            CloudBlobClient blobClient = StorageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference("kingslayer");
            CloudBlockBlob blockBlob = container.GetBlockBlobReference(Guid.NewGuid().ToString());
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content ?? "")))
            {
                blockBlob.UploadFromStream(stream);
            }
            return blockBlob.Uri.ToString();
        }

        private static string GetDisambigousPage(string assetId, Tuple<string, string>[] duplicatedDocuments)
        {
            IDictionary<string, string> mappingProduction = new Dictionary<string, string>
            {
                { "VS.core-docs", ".Net Core" },
                { "MSDN.aspnetAPIDocs", "ASP .Net Core" }
            };
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(File.ReadAllText("..\\..\\disambigouspage.html"));
            var targetH1Nodes = doc.DocumentNode.SelectNodes(@"//div[@id='main']//h1//span");
            var name = NormalizeName(assetId);
            foreach (var node in targetH1Nodes)
            {
                node.InnerHtml = name;
            }

            var targetTableBody = doc.DocumentNode.SelectSingleNode(@"//table[@id='targetTable']//tbody");
            var tbody = "";
            foreach (var duplicatedDocument in duplicatedDocuments)
            {
                tbody += $"<tr><td><a href='{duplicatedDocument.Item2}'>{name}</a></td>" +
                         $"<td>{mappingProduction[duplicatedDocument.Item1]}</td></tr>";
            }
            targetTableBody.InnerHtml = tbody;
            return doc.DocumentNode.OuterHtml;
        }

        private static string NormalizeName(string name)
        {
            return string.Join(".", name.Split('.').Select(segment => segment[0].ToString().ToUpper() + segment.Substring(1)));
        }

        private static void ReplaceToc(string tocString)
        {
            string[] targetTocs = {"toc.json"};
            foreach (var toc in targetTocs)
            {
                CancellationToken cancellationToken = new CancellationToken();
                GetDocumentResponse document = Client.GetDocument(newDepotName, toc, "en-us", 0, "master", false, null, cancellationToken).Result;
                if (toc.Split('/').Length > 1)
                {
                    var json = JsonConvert.DeserializeObject<JArray>(tocString);
                    Traverse(json, null);
                    tocString = JsonConvert.SerializeObject(json);
                }
                Client.PutDocument(newDepotName, toc, "en-us", 0, "master", new PutDocumentRequest
                {
                    Metadata = document.Metadata,
                    ContentSourceUri = GetBlobUrlByCreatingOne(tocString)
                }, null, cancellationToken).Wait();
            }
        }

        private static void CreateDepot()
        {
            CancellationToken cancellationToken = new CancellationToken();
            GetDepotResponse depot = Client.GetDepot("MSDN.coredocs-demo", null, cancellationToken).Result;
            PutDepotRequest request = new PutDepotRequest
            {
                SiteBasePath = $"ppe.docs.microsoft.com/{newDepotBasePath}/",
                Tenant = depot.Tenant,
                Metadata = depot.Metadata
            };
            request.Metadata["docset_path"] = $"/{newDepotBasePath}";
            Client.PutDepot(newDepotName, request, null, cancellationToken).Wait();
            Client.PutBranch(newDepotName, "master", null, cancellationToken).Wait();
        }

        private static void GetMergedRepo(IDocumentHostingService sandboxClient, IDocumentHostingService prodClient)
        {
            string[] depotNames =
            {
                //"MSDN.azuredotnet",
                //"MSDN.aspnetAPIDocs",
                //"MSDN.coredocs-demo",
                // Prod
                "MSDN.aspnetAPIDocs",
                "VS.core-docs",
            };
            CancellationToken cancellationToken = new CancellationToken();
            foreach (var depotName in depotNames)
            {
                Console.WriteLine(depotName);
                string continueAt = null;
                int count = 0;
                while (true)
                {
                    GetDocumentsResponse documents = prodClient.GetDocumentsPaginated(depotName, "en-us", "master", false, continueAt, null, 100, cancellationToken).Result;
                    continueAt = documents.ContinueAt;
                    count += documents.Documents.Count;
                    Console.WriteLine($"{count}, {DateTime.Now:HH:mm:ss tt zz}");

                    foreach (var document in documents.Documents)
                    {
                        var key = document.AssetId;
                        if (key.StartsWith(HardCodedPrefix))
                        {
                            key = key.Substring(HardCodedPrefix.Length);
                        }
                        document.AssetId = key;
                        if (!DocumentDict.ContainsKey(key))
                        {
                            DocumentDict.Add(key, new List<GetDocumentResponse>());
                        }
                        DocumentDict[key].Add(document);
                    }
                    
                    var putDocumentsRequest = new PutDocumentsRequest();
                    putDocumentsRequest.Documents.AddRange(documents.Documents.Select(d => new PutDocumentsRequestItem
                    {
                        AssetId = d.AssetId,
                        ProductVersion = d.ProductVersion,
                        ContentSourceUri = GetCorrectedCanonicalUrl(d.ContentUri, d.AssetId),
                        //ContentSourceUri = d.ContentUri,
                        Locale = d.Locale,
                        Metadata = d.Metadata
                    }));

                    sandboxClient.PutDocuments(newDepotName, "master", putDocumentsRequest, null, cancellationToken).Wait();
                    
                    if (string.IsNullOrEmpty(continueAt)) break;
                }
            }
            Console.WriteLine("Merge comeplete. Press any key to continue...");
        }

        private static string GetCorrectedCanonicalUrl(string url, string assetId)
        {
            if (assetId.Contains("/")) return url;
            HtmlDocument doc = new HtmlDocument();
            using (WebClient client = new WebClient())
            {
                string s = client.DownloadString(url);
                doc.LoadHtml(s);
                var canonicalLink = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']");
                if (canonicalLink != null)
                {
                    canonicalLink.Attributes["href"].Value = $"https://ppe.docs.microsoft.com/en-us/{newDepotBasePath}/{assetId}";
                    var result = doc.DocumentNode.OuterHtml;
                    result = result.Replace("../", "");
                    return GetBlobUrlByCreatingOne(result);
                }
            }

            return url;
        }

        private static string GetMergedToc()
        {
            string[] tocUrls = {
                "https://docs.microsoft.com/en-us/aspnet/core/api/toc.json",
                "https://docs.microsoft.com/en-us/dotnet/core/api/toc.json",
            };

            JArray[] tocJsons = tocUrls.Select(url =>
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        var jsonStr = client.GetStringAsync(url).Result;
                        var json = JsonConvert.DeserializeObject<JArray>(jsonStr);
                        return json;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to load the toc File: {0}", ex.Message);
                }
                return null;
            }).ToArray();

            JArray mergedToc = MergeToc(tocJsons);
            string result = JsonConvert.SerializeObject(mergedToc);

            using (StreamWriter file = new StreamWriter(File.Create("stat_result.txt")))
            {
                foreach (var title in CountDict.Keys)
                {
                    if (CountDict[title] > 1)
                    {
                        file.WriteLine($"{title}: {CountDict[title]}");
                    }
                }
            }


            using (StreamWriter file = new StreamWriter(File.Create("result.json")))
            {
                file.WriteLine(result);
            }
            return result;
        }

        private static JArray MergeToc(IEnumerable<JArray> tocJsons)
        {
            JArray result = new JArray();
            Dictionary<string, JObject> hs = new Dictionary<string, JObject>();
            foreach (var toc in tocJsons)
            {
                Traverse(toc);
                foreach (var child in toc.Children<JObject>())
                {
                    var tocTitle = ((JObject)child).Property("toc_title").Value.ToString();
                    if (!hs.ContainsKey(tocTitle))
                    {
                        hs.Add(tocTitle, child);
                    }
                    else
                    {
                        var children = hs[tocTitle].Property("children").Value as JArray;
                        var newChildren = child.Property("children").Value as JArray;
                        if (children != null && newChildren != null)
                        {
                            foreach (var item in newChildren)
                            {
                                children.Add(item);
                            }
                        }
                    }
                }
            }
            foreach (var item in hs.Values)
            {
                result.Add(item);
            }
            return result;
        }

        private static void Traverse(object root, string prefix = null)
        {
            if (root is JArray)
            {
                foreach (var child in ((JArray)root).Children())
                {
                    Traverse(child, prefix);
                }
            }
            else if (root is JObject)
            {
                var tocTitle = ((JObject)root).Property("toc_title").Value.ToString();
                var children = ((JObject)root).Property("children").Value;

                var href = ((JObject)root).Property("href").Value;
                if (href.ToString().StartsWith(HardCodedPrefix))
                {
                    ((JObject) root).Property("href").Value = href.ToString().Substring(HardCodedPrefix.Length);
                }

                if (children != null && children.Any())
                {
                    Traverse(children, tocTitle + ".");
                }
                if (!string.IsNullOrEmpty(prefix))
                {
                    tocTitle = prefix + tocTitle;
                }

                if (!CountDict.ContainsKey(tocTitle))
                {
                    CountDict.Add(tocTitle, 1);
                }
                else
                {
                    CountDict[tocTitle] += 1;
                }
            }
        }
    }
}
