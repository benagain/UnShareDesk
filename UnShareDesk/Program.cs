using Microsoft.Extensions.Configuration;
using Polly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using ZendeskApi_v2;
using ZendeskApi_v2.Models.Users;

namespace UnShareDesk
{
    internal class Program
    {
        private static IZendeskApi api;
        private static readonly AsyncPolicy policy = DefineAndRetrieveResiliencyStrategy();

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
                configuration.GetSection("Zendesk").Bind(settings);

                api = new ZendeskApi("https://esfa.zendesk.com/", settings.UserName, settings.UserPassword);

                await UnShare();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        private static async Task UnShare()
        {
            Console.WriteLine("Searching for users ");
            var control = (await policy.ExecuteAndCaptureAsync(() => api.Search.SearchForAsync<User>("created>2019-07-20"))).Result;
            Console.WriteLine($"\nFixing {control.Count} users ");

            for (int i = 0; i < control.TotalPages; ++i)
            {
                var users = (await policy.ExecuteAndCaptureAsync(() => api.Search.SearchForAsync<User>("created>2019-07-20", page: i))).Result;

                foreach (var u in users.Results)
                {
                    if (u.SharedPhoneNumber == true)
                    {
                        u.SharedPhoneNumber = false;
                        await policy.ExecuteAsync(() => api.Users.UpdateUserAsync(u));
                    }
                }
            }
        }

        private static AsyncPolicy DefineAndRetrieveResiliencyStrategy()
        {
            return Policy
                .Handle<WebException>(e =>
                {
                    return true;
                })
                .WaitAndRetryAsync(10, // Retry 10 times with a delay between retries before ultimately giving up
                    attempt => TimeSpan.FromSeconds(10 * Math.Pow(2, attempt)), // Back off!  2, 4, 8, 16 etc times 1/4-second
                    (exception, calculatedWaitDuration) =>
                    {
                        Console.WriteLine($"{exception.Message}\nAutomatically delaying for {calculatedWaitDuration.TotalMilliseconds}ms");
                    }
                );
        }
    }

    public static class StringExtensions
    {
        public static string Join<T>(this IEnumerable<T> values, string separator) => string.Join(separator, values);
    }
}