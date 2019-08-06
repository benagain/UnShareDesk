using Microsoft.Extensions.Configuration;
using MoreLinq;
using Polly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ZendeskApi_v2;
using ZendeskApi_v2.Models;
using ZendeskApi_v2.Models.Organizations;
using ZendeskApi_v2.Models.Search;
using ZendeskApi_v2.Models.Users;

namespace CleanDesk
{
    internal class Program
    {
        private static readonly AsyncPolicy policy = DefineAndRetrieveResiliencyStrategy();
        private static int dotCount = 0;

        private class Settings
        {
            public string UserName { get; set; }
            public string UserPassword { get; set; }
        }

        private static async Task Main(string[] args)
        {
            try
            {
                var builder = new ConfigurationBuilder()
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                   .AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: true);

                var configuration = builder.Build();
                var mySettingsConfig = new Settings();
                configuration.GetSection("MySettings").Bind(mySettingsConfig);

                await CleanDesk(mySettingsConfig);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        private static async Task CleanDesk(Settings settings)
        {
            var api = new ZendeskApi("https://esfa.zendesk.com/", settings.UserName, settings.UserPassword);

            // Orgs
            Console.Write("Searching for orgs ");
            var orgs = (await SearchAll<Organization>(api))
                .OrderBy(x => x.Name);

            Console.Write("\nDeleting orgs ");
            await Delete(orgs, api);

            //var displayOrgs = orgs.Select(x => $"{x.Id:000000000000}: {x.Name}").ToList();
            //Console.WriteLine($"\n\nOrganisations:\n{string.Join("\n", displayOrgs)}");

            // Users
            Console.Write("Searching for users ");
            var users = (await SearchAll<User>(api))
                .Where(x => x.Role == "end-user")
                .OrderBy(x => x.Name);

            Console.Write("\nDeleting users ");
            await Delete(users, api);

            //var displayUsers = users.Select(x => $"{x.Id:000000000000}: {x.Name}").ToList();
            //Console.WriteLine($"\n\nUsers:\n{string.Join("\n", displayUsers)}");
        }

        private static async Task Delete(IOrderedEnumerable<User> users, IZendeskApi api)
        {
            dotCount = 0;
            foreach (var o in users.Batch(500))
            {
                var q = o.Select(x => x.Id).Join(",");
                await policy.ExecuteAsync(() => api.Users.BulkDeleteUsersAsync(o));
                Dot();
            }

            Console.WriteLine($"Deleted {users.Count()} users");
        }

        private static async Task Delete(IOrderedEnumerable<Organization> users, IZendeskApi api)
        {
            dotCount = 0;
            foreach (var o in users.Batch(500))
            {
                var q = o.Select(x => x.Id).Join(",");
                await policy.ExecuteAsync(() => BulkDeleteOrganisationsAsync(o));
                Dot();
            }

            Console.WriteLine($"Deleted {users.Count()} organisations");
        }

        private static readonly HttpClient httpClient = MakeHttpClient();

        private static HttpClient MakeHttpClient()
        {
            var httpClient = new HttpClient();
            var byteArray = Encoding.ASCII.GetBytes("ben.arnold@digital.education.gov.uk/token:b4SMhio3QwUbAGiSaLzE6k9Y8jAd5qHA3zowWjqh");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            return httpClient;
        }

        private static async Task BulkDeleteOrganisationsAsync(IEnumerable<Organization> o)
        {
            var query = o.Select(x => x.Id).Join(",");
            var uri = $"https://esfa.zendesk.com/api/v2/organizations/destroy_many.json?ids={query}";
            await httpClient.DeleteAsync(uri);
        }

        private static AsyncPolicy DefineAndRetrieveResiliencyStrategy()
        {
            return Policy
                .Handle<WebException>()
                .WaitAndRetryAsync(10, // Retry 10 times with a delay between retries before ultimately giving up
                    attempt => TimeSpan.FromSeconds(10 * Math.Pow(2, attempt)), // Back off!  2, 4, 8, 16 etc times 1/4-second
                    (exception, calculatedWaitDuration) =>
                    {
                        Console.WriteLine($"Server is throttling our requests. Automatically delaying for {calculatedWaitDuration.TotalMilliseconds}ms");
                    }
                );
        }

        private static void Dot()
        {
            ++dotCount;

            var ch =
                dotCount % 100 == 0 ? "|"
                : dotCount % 10 == 0 ? "-"
                : ".";

            Console.Write(ch);
        }

        private static async Task<IEnumerable<T>> SearchAll<T>(ZendeskApi api) where T : ISearchable
        {
            var totalPages = (await api.Search.SearchForAsync<T>("created>2019-07-20")).TotalPages;

            var tasks = new List<Task<PolicyResult<SearchResults<T>>>>();

            for (var i = 0; i < totalPages; ++i)
            {
                var task = policy.ExecuteAndCaptureAsync(() => api.Search.SearchForAsync<T>("created>2019-07-20", page: i));

                tasks.Add(task.ContinueWith(x => { Dot(); return x.Result; }));
            }

            return (await Task.WhenAll(tasks))
                .SelectMany(x => x.Result.Results);
        }
    }

    public static class StringExtensions
    {
        public static string Join<T>(this IEnumerable<T> values, string separator) => string.Join(separator, values);
    }
}