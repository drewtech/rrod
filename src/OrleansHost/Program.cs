﻿using GrainInterfaces;
using Grains;
using Grains.Redux;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers.Streams.AzureQueue;

namespace OrleansHost
{
    internal class Program
    {
        private static ISiloHost silo;
        private static readonly ManualResetEvent SiloStopped = new ManualResetEvent(false);
        private static readonly LoggerFactory LoggerFactory = new LoggerFactory();

        private static async Task Main(string[] args)
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddInMemoryCollection(new Dictionary<string, string> // add default settings, that will be overridden by commandline
                    {
                        {"Id", "OrleansHost"},
                        {"Version", "1.0.0"},
                        {"ClusterId", "rrod-cluster"},
                    })
                .AddCommandLine(args)
                .AddJsonFile("OrleansHost.settings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"OrleansHost.settings.{environment}.json", optional: true, reloadOnChange: true)
                .AddJsonFile("/run/config/OrleansHost.settings.json", optional: true, reloadOnChange: true)
                .AddDockerSecrets("/run/secrets", optional: true)   // we can pas connectionstring as a docker secret
                .AddUserSecrets<Program>(optional: true)            // for development
                .AddEnvironmentVariables("RROD_")                   // can override all settings (i.e. URLS) by passing an environment variable
                .Build();  

            LoggerFactory.AddConsole(config.GetSection("Logging"));
            LoggerFactory.AddDebug();
            var logger = LoggerFactory.CreateLogger<Program>();

            logger.LogWarning($"Starting Orleans silo in {environment} environment...");

            foreach (var provider in config.Providers)
            {
                logger.LogInformation($"Config Provider {provider.GetType().Name}: {provider.GetChildKeys(Enumerable.Empty<string>(), null).Count()} settings");
            }

            silo = new SiloHostBuilder()
                .ConfigureSiloName(config["Id"])
                .Configure(options => options.ClusterId = config["ClusterId"])
                .ConfigureServices((context, services) =>
                {
                    services.AddOptions();
                    services.TryAdd(ServiceDescriptor.Singleton<ILoggerFactory, LoggerFactory>());
                    services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
                    services.Configure<ConnectionStrings>(config.GetSection("ConnectionStrings"));
                    var reduxConnectionString = config.GetConnectionString("ReduxConnectionString");
                    services.AddSingleton(new ReduxTableStorage<CertState>(reduxConnectionString));
                    services.AddSingleton(new ReduxTableStorage<UserState>(reduxConnectionString));
                    services.AddSingleton(new ReduxTableStorage<CounterState>(reduxConnectionString));
                    services.AddSingleton(new ReduxTableStorage<StringStoreState>(reduxConnectionString));
                })
                .ConfigureApplicationParts(parts =>
                {
                    parts.AddApplicationPart(typeof(CounterGrain).Assembly).WithReferences();
                    parts.AddApplicationPart(typeof(AzureQueueDataAdapterV2).Assembly).WithReferences();
                })
                .AddAzureTableGrainStorageAsDefault(options => options.ConnectionString = config.GetConnectionString("DataConnectionString"))
                .UseAzureStorageClustering(options => options.ConnectionString = config.GetConnectionString("DataConnectionString"))
                .UseAzureTableReminderService(options => options.ConnectionString = config.GetConnectionString("DataConnectionString"))
                .AddAzureTableGrainStorage("PubSubStore", options => options.ConnectionString = config.GetConnectionString("DataConnectionString"))
                .AddAzureQueueStreams<AzureQueueDataAdapterV2>("Default", options =>
                {
                    options.ConnectionString = config.GetConnectionString("DataConnectionString");
                    options.ClusterId = config["ClusterId"];
                })
                .Build();

            await StartSilo();

            AssemblyLoadContext.Default.Unloading += context =>
            {
                Task.Run(StopSilo);
                SiloStopped.WaitOne();
            };

            SiloStopped.WaitOne();
        }

        private static async Task StartSilo()
        {
            try
            {
                await silo.StartAsync();
                Console.WriteLine("Silo started");
            }
            catch (OrleansLifecycleCanceledException e)
            {
                Console.WriteLine("Silo could not be started with exception: " + e.InnerException.Message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Silo could not be started with exception: " + e.Message);
            }
        }

        private static async Task StopSilo()
        {
            await silo.StopAsync();
            Console.WriteLine("Silo stopped");
            SiloStopped.Set();
        }
    }
}