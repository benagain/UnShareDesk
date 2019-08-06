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
        private static IZendeskApi api;
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
                var settings = new Settings();
                configuration.GetSection("Settings").Bind(settings);

                api = new ZendeskApi("https://esfa.zendesk.com/", settings.UserName, settings.UserPassword);
                httpClient = MakeHttpClient(settings);

                //*
                await CleanDesk(settings);
                /**/
                await ReportDesk(settings);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        private static async Task ReportDesk(Settings settings)
        {
            int top = Console.CursorTop;

            const int pauseSeconds = 240;
            var pauseTicks = pauseSeconds * 10;
            double width = Convert.ToDouble(Console.WindowWidth);
            var wTick = width / pauseTicks;

            for (; ; )
            {
                {
                    var u = await InitialSearch<User>();
                    Console.WriteLine($"Zendesk reports {u.Count} users");

                    var o = await InitialSearch<Organization>();
                    Console.WriteLine($"Zendesk reports {o.Count} organisations");

                    if (u.Count == 0 && o.Count == 0) return;

                    for (int i = 0; i <= pauseTicks; ++i)
                    {
                        Console.SetCursorPosition(0, top + 2);
                        Console.WriteLine(new string('_', Convert.ToInt32(wTick * (pauseTicks - i))) + new string(' ', Console.WindowWidth));

                        await Task.Delay(TimeSpan.FromMilliseconds(100));
                    }

                    Console.SetCursorPosition(0, 0);
                    Console.WriteLine(new string(' ', Console.WindowWidth * 20));
                    Console.SetCursorPosition(0, 0);

                    await CleanDesk(settings);
                }
            }
        }

        private static async Task CleanDesk(Settings settings)
        {
            // Orgs
            Console.WriteLine("Searching for orgs ");
            var orgs = (await SearchAll<Organization>())
                .OrderBy(x => x.Name);

            Console.WriteLine($"\nDeleting {orgs.Count()} orgs ");
            await Delete(orgs);

            //var displayOrgs = orgs.Select(x => $"{x.Id:000000000000}: {x.Name}").ToList();
            //Console.WriteLine($"\n\nOrganisations:\n{string.Join("\n", displayOrgs)}");

            // Users
            Console.WriteLine("Searching for users ");
            var users = (await SearchAll<User>())
                .Where(x => x.Role == "end-user")
                .OrderBy(x => x.Name);

            Console.WriteLine($"\nDeleting {users.Count()} users ");
            await Delete(users);

            //var displayUsers = users.Select(x => $"{x.Id:000000000000}: {x.Name}").ToList();
            //Console.WriteLine($"\n\nUsers:\n{string.Join("\n", displayUsers)}");
        }

        private static async Task Delete(IOrderedEnumerable<User> users)
        {
            dotCount = 0;
            foreach (var o in users.Batch(500))
            {
                var q = o.Select(x => x.Id).Join(",");
                await policy.ExecuteAsync(() => api.Users.BulkDeleteUsersAsync(o));
                await Task.Delay(TimeSpan.FromSeconds(15));
                Dot();
            }

            Console.WriteLine($"Deleted {users.Count()} users");
        }

        private static async Task Delete(IOrderedEnumerable<Organization> users)
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

        private static HttpClient httpClient;

        private static HttpClient MakeHttpClient(Settings settings)
        {
            var httpClient = new HttpClient();
            var byteArray = Encoding.ASCII.GetBytes($"{settings.UserName}:{settings.UserPassword}");
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

        private static async Task<IEnumerable<T>> SearchAll<T>() where T : ISearchable
        {
            var initialResults = await InitialSearch<T>();
            var totalPages = initialResults.TotalPages;

            Console.WriteLine($"Retrieving details of {initialResults.Count} records");

            var tasks = new List<Task<PolicyResult<SearchResults<T>>>>();

            dotCount = 0;
            for (var i = 1; i <= totalPages; ++i)
            {
                var task = policy.ExecuteAndCaptureAsync(() => api.Search.SearchForAsync<T>("created>2019-07-20", page: i));

                tasks.Add(task.ContinueWith(x => { Dot(); return x.Result; }));
            }

            return (await Task.WhenAll(tasks))
                .SelectMany(x => x.Result.Results);
        }

        private static async Task<SearchResults<T>> InitialSearch<T>() where T : ISearchable
        {
            return await api.Search.SearchForAsync<T>("created>2019-07-20");
        }
    }

    public static class StringExtensions
    {
        public static string Join<T>(this IEnumerable<T> values, string separator) => string.Join(separator, values);
    }
}