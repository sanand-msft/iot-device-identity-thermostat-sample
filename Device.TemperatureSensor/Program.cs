using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Threading;
using System.Runtime.Loader;
using Microsoft.Azure.Devices.Client;
using DataGenerator;
using Microsoft.Extensions.Logging;

namespace Device.TemperatureSensor
{
    class Program
    {
        private static readonly long expiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();

        // DTDL interface used: https://github.com/Azure/iot-plugandplay-models/blob/main/dtmi/com/example/thermostat-1.json
        private const string ModelId = "dtmi:com:iotdevice:Thermostat;1";
        private static ILogger s_logger;

        static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();

            s_logger = InitializeConsoleDebugLogger();
            var identityInfo = await GetIdentityInfoAsync(cancellationToken);
            var dataToSign = GetDataToSign(identityInfo);
            var signature = await GetSignatureAsync(identityInfo, dataToSign, cancellationToken);
            var connectionString = GetFullConnectionString(identityInfo, signature);
            await StartSendingDataAsync(connectionString, cancellationToken);

            WhenCancelled(cancellationToken).Wait();
        }

        private static ILogger InitializeConsoleDebugLogger()
        {
            ILoggerFactory loggerFactory = new LoggerFactory();

            return loggerFactory.CreateLogger<ThermostatSample>();
        }

        // Step 1 Get identity information 
        private static async Task<DeviceIdentityInfo> GetIdentityInfoAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Getting Identity Info...");

            using var idHttpClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    var endpoint = new UnixDomainSocketEndPoint("/run/aziot/identityd.sock");
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            });
            var json = await idHttpClient.GetStringAsync(@"http://identityd.sock/identities/identity?api-version=2020-09-01", cancellationToken);
            Console.WriteLine($"Identity Info: {json}");

            var jObject = JObject.Parse(json);
            return JsonConvert.DeserializeObject<DeviceIdentityInfo>(jObject["spec"].ToString());
        }

        // Step 2 Build a base64-encoded resource URI that expires
        private static string GetDataToSign(DeviceIdentityInfo identityInfo)
        {
            var resource_uri = GetResourceUri(identityInfo);

            var signatureData = Encoding.UTF8.GetBytes($"{resource_uri}\n{expiry}");
            var signatureDataAsBase64 = Convert.ToBase64String(signatureData);
            Console.WriteLine($"Signature Data: {signatureDataAsBase64}");

            return signatureDataAsBase64;
        }

        // Step 3 Get a signed token from the Keys Service
        private static async Task<string> GetSignatureAsync(DeviceIdentityInfo identityInfo, string dataToSign, CancellationToken cancellationToken)
        {
            Console.WriteLine("Getting Signature from keyd");

            var payload = new
            {
                keyHandle = identityInfo.Auth.KeyHandle,
                algorithm = "HMAC-SHA256",
                parameters = new
                {
                    message = dataToSign
                }
            };

            using var keydHttpClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    var endpoint = new UnixDomainSocketEndPoint("/run/aziot/keyd.sock");
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            });
            using var request = new HttpRequestMessage(new HttpMethod("POST"), "http://keyd.sock/sign?api-version=2020-09-01");
            request.Content = new StringContent(JsonConvert.SerializeObject(payload));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

            var response = await keydHttpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var signature = JObject.Parse(json)["signature"];

            Console.WriteLine($"Signature: {signature}");
            return HttpUtility.UrlEncode(signature.ToString());
        }

        // Step 4: Create the full connection string
        private static string GetFullConnectionString(DeviceIdentityInfo identityInfo, string signature)
        {
            var sasToken = $"sr={GetResourceUri(identityInfo)}&se={expiry}&sig={signature}";
            var sas = $"SharedAccessSignature {sasToken}";

            if (identityInfo.HubName == identityInfo.GatewayHost)
            {
                return $"HostName={identityInfo.HubName};DeviceId={identityInfo.DeviceId};SharedAccessSignature={sas}";
            }
            else
            {
                return $"HostName={identityInfo.HubName};DeviceId={identityInfo.DeviceId};SharedAccessSignature={sas};GatewayHost={identityInfo.GatewayHost}";
            }
        }

        // Step 5: Start sending data to IoT Hub
        private static async Task StartSendingDataAsync(string connectionString, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Using connection string: {connectionString}");
            var options = new ClientOptions
            {
                ModelId = ModelId,
            };
            using var deviceClient = DeviceClient.CreateFromConnectionString(connectionString, TransportType.Mqtt,options);

            await deviceClient.OpenAsync(cancellationToken);
            Console.WriteLine("Device connection SUCCESS.");

            var sample = new ThermostatSample(deviceClient, s_logger);
            await sample.PerformOperationsAsync(cancellationToken);

            await deviceClient.CloseAsync();
        }

        private static string GetResourceUri(DeviceIdentityInfo identityInfo)
        {
            return HttpUtility.UrlEncode($"{identityInfo.HubName}/devices/{identityInfo.DeviceId}");
        }

        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }
    }
}
