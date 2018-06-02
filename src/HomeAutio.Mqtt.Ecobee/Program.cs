using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using I8Beef.Ecobee;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HomeAutio.Mqtt.Ecobee
{
    /// <summary>
    /// Main program entry point.
    /// </summary>
    public class Program
    {
        private static StoredAuthToken _currentAuthToken;

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <param name="args">Arguments.</param>
        public static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <param name="args">Arguments.</param>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        public static async Task MainAsync(string[] args)
        {
            // Setup logging
            Log.Logger = new LoggerConfiguration()
              .Enrich.FromLogContext()
              .WriteTo.Console()
              .WriteTo.RollingFile(@"logs/HomeAutio.Mqtt.Ecobee.log")
              .CreateLogger();

            // Valikdates existing or gets new tokens
            await ValidateTokens();

            var hostBuilder = new HostBuilder()
                .ConfigureAppConfiguration((hostContext, config) =>
                {
                    config.SetBasePath(Environment.CurrentDirectory);
                    config.AddJsonFile("appsettings.json", optional: false);
                })
                .ConfigureLogging((hostingContext, logging) =>
                {
                    logging.AddSerilog();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Setup client
                    services.AddScoped<Client>(serviceProvider =>
                    {
                        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                        return new Client(
                            configuration.GetValue<string>("ecobeeAppKey"),
                            ReadTokenFileAsync,
                            WriteTokenFileAsync);
                    });

                    // Setup service instance
                    services.AddScoped<IHostedService, EcobeeMqttService>(serviceProvider =>
                    {
                        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                        return new EcobeeMqttService(
                            serviceProvider.GetRequiredService<ILogger<EcobeeMqttService>>(),
                            serviceProvider.GetRequiredService<Client>(),
                            configuration.GetValue<string>("ecobeeName"),
                            configuration.GetValue<int>("refreshInterval"),
                            configuration.GetValue<string>("brokerIp"),
                            configuration.GetValue<int>("brokerPort"),
                            configuration.GetValue<string>("brokerUsername"),
                            configuration.GetValue<string>("brokerPassword"));
                    });
                });

            await hostBuilder.RunConsoleAsync();
        }

        /// <summary>
        /// Write token file callback.
        /// </summary>
        /// <param name="storedAuthToken">Stored auth token.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        public static async Task WriteTokenFileAsync(StoredAuthToken storedAuthToken, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Cache the returned tokens
            _currentAuthToken = storedAuthToken;

            // Write token to persistent store
            var text = new StringBuilder();
            text.AppendLine($"{storedAuthToken.TokenExpiration:MM/dd/yy hh:mm:ss tt}");
            text.AppendLine(storedAuthToken.AccessToken);
            text.AppendLine(storedAuthToken.RefreshToken);

            await File.WriteAllTextAsync(@"token.txt", text.ToString());
        }

        /// <summary>
        /// Read token file callback.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The <see cref="StoredAuthToken"/>.</returns>
        public static async Task<StoredAuthToken> ReadTokenFileAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_currentAuthToken == null && File.Exists(@"token.txt"))
            {
                var tokenText = await File.ReadAllLinesAsync(@"token.txt");
                _currentAuthToken = new StoredAuthToken
                {
                    TokenExpiration = DateTime.Parse(tokenText[0]),
                    AccessToken = tokenText[1],
                    RefreshToken = tokenText[2]
                };
            }

            return _currentAuthToken;
        }

        /// <summary>
        /// Validates current, or gets new auth tokens.
        /// </summary>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private static async Task ValidateTokens()
        {
            // Configuration
            var registrationConfig = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json")
                .Build();

            // initialize tokens
            var registrationClient = new Client(
                registrationConfig.GetValue<string>("ecobeeAppKey"),
                ReadTokenFileAsync,
                WriteTokenFileAsync);
            if (!File.Exists(@"token.txt"))
            {
                Console.WriteLine("Getting new tokens");
                var pin = await registrationClient.GetPinAsync();

                Console.WriteLine("Pin: " + pin.EcobeePin);
                Console.WriteLine("You have " + pin.ExpiresIn + " minutes to enter this on the Ecobee site and hit enter.");

                Console.ReadLine();

                await registrationClient.GetAccessTokenAsync(pin.Code);
            }
            else
            {
                Console.WriteLine("Loading existing tokens");
                var storedAuthToken = await ReadTokenFileAsync();
            }
        }
    }
}
