using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BookOfUrlsPOCdotnetfull
{
    public class Program
    {
        static Dictionary<string, int> countDict = new Dictionary<string, int>();

        public static void Main(string[] args)
        {
            string[] depotNames = { "MSDN.azuredotnet" };

        }

        private static void GetMergedToc()
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
                foreach (var title in countDict.Keys)
                {
                    if (countDict[title] > 1)
                    {
                        file.WriteLine($"{title}: {countDict[title]}");
                    }
                }
            }


            using (StreamWriter file = new StreamWriter(File.Create("result.json")))
            {
                file.WriteLine(result);
            }
            Console.WriteLine("Toc merged. Press any key to continue...");
            Console.ReadKey();
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

                if (!countDict.ContainsKey(tocTitle))
                {
                    countDict.Add(tocTitle, 1);
                }
                else
                {
                    countDict[tocTitle] += 1;
                }
            }
        }
    }
}
