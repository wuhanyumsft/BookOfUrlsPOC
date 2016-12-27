using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Document.Hosting.RestClient;
using Microsoft.Document.Hosting.RestService.Contract;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Linq;

namespace BookOfUrlsPOCdotnetfull
{
    public class Program
    {
        static readonly Dictionary<string, int> CountDict = new Dictionary<string, int>();
        static readonly Dictionary<string, IList<GetDocumentResponse>> DocumentDict = new Dictionary<string, IList<GetDocumentResponse>>();
        static readonly CloudStorageAccount StorageAccount = CloudStorageAccount.Parse("");
        static readonly IDocumentHostingService Client = new DocumentHostingServiceClient(
                new Uri("https://op-dhs-sandbox-pub.azurewebsites.net"),
                "integration_test",
                "",
                TimeSpan.FromSeconds(10),
                null); 

        public static void Main(string[] args)
        {
            //CreateDepot();
            GetMergedRepo();
            var blobUrl = GetMergedToc();
            ReplaceToc(blobUrl);
            string newDepotName = "MSDN.test.api";
            CancellationToken cancellationToken = new CancellationToken();
            HashSet<string> blackList = new HashSet<string> { "index", "toc.json", "_themes" };
            var duplicatedDcouments = DocumentDict.Where(e => !blackList.Contains(e.Key.Split('/').First()) && e.Value.Count > 1);
            foreach (var duplicatedDocument in duplicatedDcouments)
            {
                Console.WriteLine(duplicatedDocument.Key);
                var disabmbigousPage = GetDisambigousPage(duplicatedDocument.Key,
                    duplicatedDocument.Value.Select(
                        v => Tuple.Create<string, string>(v.DepotName, $"{v.AssetId}({v.DepotName})")).ToArray());
                var url = GetBlobUrlByCreatingOne(disabmbigousPage);
                var putDisambigousPageRequest = new PutDocumentRequest
                {
                    Metadata = new Dictionary<string, object>(),
                    ContentSourceUri = url
                };
                foreach (var item in duplicatedDocument.Value)
                {
                    var putDocumentRequest = new PutDocumentRequest
                    {
                        Metadata = item.Metadata,
                        ContentSourceUri = item.ContentUri
                    };
                    Client.PutDocument(newDepotName, $"{item.AssetId}({item.DepotName})", item.Locale,
                        item.ProductVersion, "master", putDocumentRequest, null, cancellationToken).Wait();
                    putDisambigousPageRequest.Metadata = item.Metadata;
                    Client.PutDocument(newDepotName, item.AssetId, item.Locale,
                        item.ProductVersion, "master", putDisambigousPageRequest, null, cancellationToken).Wait();
                }


            }
            Console.ReadKey();
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
            var body = "";
            foreach (var duplicatedDocument in duplicatedDocuments)
            {
                body += $"<p><a href='{duplicatedDocument.Item2}'>{assetId} in {duplicatedDocument.Item1}</a></p>";
            }
            return $"<head><metahttp-equiv=\"content-type\"content=\"text/html;charset=utf-8\"><title>HelloWorld</title></head><body>{body}</body></html>";
        }

        private static void ReplaceToc(string blobUrl)
        {
            string[] targetTocs = {"toc.json"};
            foreach (var toc in targetTocs)
            {
                CancellationToken cancellationToken = new CancellationToken();
                string newDepotName = "MSDN.test.api";
                GetDocumentResponse document = Client.GetDocument(newDepotName, toc, "en-us", 0, "master", false, null, cancellationToken).Result;
                Client.PutDocument(newDepotName, toc, "en-us", 0, "master", new PutDocumentRequest
                {
                    Metadata = document.Metadata,
                    ContentSourceUri = blobUrl
                }, null, cancellationToken).Wait();
            }
        }

        private static void CreateDepot()
        {
            string newDepotName = "MSDN.test.api";
            CancellationToken cancellationToken = new CancellationToken();
            GetDepotResponse depot = Client.GetDepot("MSDN.coredocs-demo", null, cancellationToken).Result;
            PutDepotRequest request = new PutDepotRequest
            {
                SiteBasePath = "ppe.docs.microsoft.com/test.api/",
                Tenant = depot.Tenant,
                Metadata = depot.Metadata
            };
            request.Metadata["docset_path"] = "/test.api";
            Client.PutDepot(newDepotName, request, null, cancellationToken).Wait();
            Client.PutBranch(newDepotName, "master", null, cancellationToken).Wait();
        }

        private static void GetMergedRepo()
        {
            string[] depotNames =
            {
                "MSDN.azuredotnet",
                "MSDN.coredocs-demo",
                "MSDN.aspnetAPIDocs"
            };
            string newDepotName = "MSDN.test.api";
            CancellationToken cancellationToken = new CancellationToken();
            foreach (var depotName in depotNames)
            {
                Console.WriteLine(depotName);
                string continueAt = null;
                int count = 0;
                while (true)
                {
                    GetDocumentsResponse documents = Client.GetDocumentsPaginated(depotName, "en-us", "master", false, continueAt, null, 100, cancellationToken).Result;
                    continueAt = documents.ContinueAt;
                    count += documents.Documents.Count;
                    Console.WriteLine($"{count}, {DateTime.Now:HH:mm:ss tt zz}");

                    var putDocumentsRequest = new PutDocumentsRequest();
                    putDocumentsRequest.Documents.AddRange(documents.Documents.Select(d => new PutDocumentsRequestItem
                    {
                        AssetId = d.AssetId,
                        ProductVersion = d.ProductVersion,
                        ContentSourceUri = d.ContentUri,
                        Locale = d.Locale,
                        Metadata = d.Metadata
                    }));

                    foreach (var document in documents.Documents)
                    {
                        var key = document.AssetId;
                        if (key.StartsWith("api/"))
                        {
                            key = key.Substring(4);
                        }
                        if (!DocumentDict.ContainsKey(key))
                        {
                            DocumentDict.Add(key, new List<GetDocumentResponse>());
                        }
                        DocumentDict[key].Add(document);
                    }

                    Client.PutDocuments(newDepotName, "master", putDocumentsRequest, null, cancellationToken).Wait();
                    if (string.IsNullOrEmpty(continueAt)) break;
                }
            }
            Console.WriteLine("Merge comeplete. Press any key to continue...");
        }

        private static string GetMergedToc()
        {
            string[] tocUrls = {
                "https://docs.microsoft.com/en-us/aspnet/core/api/toc.json",
                "https://docs.microsoft.com/en-us/dotnet/core/api/toc.json",
                "https://docs.microsoft.com/en-us/dotnet/api/toc.json",
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

            //hard code to add /api prefix
            Traverse(tocJsons[1], null, "api/");
            //hard code end

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
            return GetBlobUrlByCreatingOne(result);
        }

        private static JArray MergeToc(IEnumerable<JArray> tocJsons)
        {
            JArray result = new JArray();
            foreach (var toc in tocJsons)
            {
                Traverse(toc);
                foreach (var child in toc.Children<JObject>())
                {
                    result.Add(child);
                }
            }
            return result;
        }

        private static void Traverse(object root, string prefix = null, string addPrefix = null)
        {
            if (root is JArray)
            {
                foreach (var child in ((JArray)root).Children())
                {
                    Traverse(child, prefix, addPrefix);
                }
            }
            else if (root is JObject)
            {
                var tocTitle = ((JObject)root).Property("toc_title").Value.ToString();
                var children = ((JObject)root).Property("children").Value;
                if (!string.IsNullOrEmpty(addPrefix))
                {
                    ((JObject) root).Property("href").Value = addPrefix + ((JObject) root).Property("href").Value;
                }
                if (children != null && children.Any())
                {
                    Traverse(children, tocTitle + ".", addPrefix);
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
