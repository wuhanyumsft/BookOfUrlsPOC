using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Document.Hosting.RestClient;
using Microsoft.Document.Hosting.RestService.Contract;
using Newtonsoft.Json.Linq;

namespace BookOfUrlsPOCdotnetfull
{
    public class Program
    {
        static readonly Dictionary<string, int> CountDict = new Dictionary<string, int>();
        static readonly IDocumentHostingService Client = new DocumentHostingServiceClient(
                new Uri("https://op-dhs-sandbox-pub.azurewebsites.net"),
                "integration_test",
                "",
                TimeSpan.FromSeconds(10),
                null); 

        public static void Main(string[] args)
        {
            CancellationToken cancellationToken = new CancellationToken();
            string newDepotName = "MSDN.test.api";
            GetDocumentResponse document = Client.GetDocument(newDepotName, "toc.json", "en-us", 0, "master", false, null, cancellationToken).Result;
            Client.PutDocument(newDepotName, "toc.json", "en-us", 0, "master", new PutDocumentRequest
            {
                Metadata = document.Metadata,
                ContentSourceUri = "https://opdhsblobsandbox02.blob.core.windows.net/contents/00c82de5872e4beeb44c1c5ee72aee54/result.json"
            }, null, cancellationToken).Wait();
        }

        private static void GetMergedRepo()
        {
            string[] depotNames = { "MSDN.azuredotnet", "MSDN.coredocs-demo", "MSDN.aspnetAPIDocs" };
            string newDepotName = "MSDN.test.api";
            CancellationToken cancellationToken = new CancellationToken();
            foreach (var depotName in depotNames)
            {
                Console.WriteLine(depotName);
                GetDepotResponse depot = Client.GetDepot(depotName, null, cancellationToken).Result;
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

                    Client.PutDocuments(newDepotName, "master", putDocumentsRequest, null, cancellationToken).Wait();
                    if (string.IsNullOrEmpty(continueAt)) break;
                }



                /*PutDepotRequest request = new PutDepotRequest
                {
                    SiteBasePath = "ppe.docs.microsoft.com/test/api/",
                    Tenant = depot.Tenant,
                    Metadata = depot.Metadata
                };
                request.Metadata["docset_path"] = "/test/api";
                Client.PutDepot(newDepotName, request, null, cancellationToken).Wait();
                Client.PutBranch(newDepotName, "master", null, cancellationToken).Wait();*/
            }
            Console.WriteLine("Merge comeplete. Press any key to continue...");
        }

        private static string GetMergedToc()
        {
            string[] tocUrls = {
                "https://docs.microsoft.com/en-us/dotnet/core/api/toc.json",
                "https://docs.microsoft.com/en-us/dotnet/api/toc.json",
                "https://docs.microsoft.com/en-us/aspnet/core/api/toc.json"
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
