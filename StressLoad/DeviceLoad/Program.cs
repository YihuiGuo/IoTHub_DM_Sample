﻿using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using StressLoad;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DeviceLoad
{
    class Program
    {
        static Setting setting;
        //static ResultUpdater resultUpdater;
        static MessagePereadController messagePereadController = null;

        // Example:
        // DeviceLoad.exe {OutputStorageConnectionString} {DeviceClientEndpoint} {DevicePerVm} {MessagePerMin} {DurationInMin} {BatchJobId} {DeviceIdPrefix} [MessageFormat]
        // OutputStorageConnectionString: DefaultEndpointsProtocol=https;AccountName={name};AccountKey={key};EndpointSuffix={core.windows.net}
        // DeviceClientEndpoint: HostName={http://xx.chinacloudapp.cn};SharedAccessKeyName=owner;SharedAccessKey={key}
        static void Main(string[] args)
        {
            setting = Setting.Parse(args);
            if (setting == null)
            {
                Environment.Exit(-1);
            }

            ServicePointManager.DefaultConnectionLimit = 200;
            //resultUpdater = new ResultUpdater(setting.OutputStorageConnectionString, setting.BatchJobId);

            var devices = CreateDevices().Result;

            SendMessages(devices).Wait();
            //resultUpdater.Finish().Wait();

            // No need to delete devices
            //RemoveDevices(devices).Wait();
        }

        static async Task<List<Device>> CreateDevices()
        {
            var sw = Stopwatch.StartNew();
            var createdDevices = new List<Device>();

            var devices = new List<Device>();
            for (int i = 0; i < setting.DevicePerVm; i++)
            {
                devices.Add(GenerateDevice(i));

                if (devices.Count == 100)
                {
                    createdDevices.AddRange(devices);

                    await AddDevices(devices);
                    devices.Clear();

                    Console.WriteLine($"{sw.Elapsed.TotalSeconds}:Created {createdDevices.Count} devices.");
                }
            }

            if (devices.Count > 0)
            {
                createdDevices.AddRange(devices);
                await AddDevices(devices);
                devices.Clear();
            }

            Console.WriteLine($"{sw.Elapsed.TotalSeconds}:Created {createdDevices.Count} devices.");
            return createdDevices;
            //return devices.Select(d => Tuple.Create<string, string>(d.Id, DeviceConnectionString(setting.IoTHubHostName, d)));
        }

        private static async Task AddDevices(List<Device> devices)
        {
            while (true)
            {
                try
                {
                    var result = await setting.IotHubManager.AddDevices2Async(devices);
                    if (result.Errors == null ||
                        result.Errors.Length == 0 ||
                        result.Errors[0].ErrorCode == Microsoft.Azure.Devices.Common.Exceptions.ErrorCode.DeviceAlreadyExists)
                    {
                        break;
                    }

                    Console.WriteLine($"Create failed: {result.Errors[0].ErrorStatus}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Create failed: {ex.Message}.");
                }
                
                Task.Delay(100).Wait();

            };
        }

        private static Device GenerateDevice(int i)
        {
            var device = new Device(DeviceIdGenerate(i))
            {
                Authentication = new AuthenticationMechanism
                {
                    SymmetricKey = new SymmetricKey
                    {
                        PrimaryKey = DefaultKey.Primary,
                        SecondaryKey = DefaultKey.Secondary
                    }
                }
            };

            return device;
        }

        private static async Task RemoveDevices(List<Device> devices)
        {
            var sw = Stopwatch.StartNew();

            long total = devices.Count;
            while ( devices.Count > 0)
            {
                var length = devices.Count < 100 ? devices.Count : 100;
                await setting.IotHubManager.RemoveDevices2Async(devices.GetRange(0, length));
                devices.RemoveRange(0, length);

                Console.WriteLine($"{sw.Elapsed.TotalSeconds}:Removed {length}/{total} devices.");
            }
        }

        private static string DeviceIdGenerate(int index)
        {
            return setting.DeviceIdPrefix + "-" + index.ToString().PadLeft(10, '0');
        }

        static async Task RemoveDevices(IEnumerable<Tuple<string, string>> devices)
        {
            var tasks = new List<Task>();
            foreach (var device in devices)
            {
                tasks.Add(setting.IotHubManager.RemoveDeviceAsync(device.Item1));
            }
            await Task.WhenAll(tasks.ToArray());
            Console.WriteLine(string.Format("{0} devices removed.", devices.Count()));
        }


        static async Task SendMessages(IEnumerable<Device> devices)
        {
            Console.WriteLine($"{DateTime.Now.ToString("T")}: Start to send msg.");

            var tasks = new List<Task>();
            foreach (var device in devices)
            {
                tasks.Add(DeviceToCloudMessage(device.Id, DeviceConnectionString(setting.IoTHubHostName, device)));
                await Task.Delay(100);
            }

            await Task.WhenAll(tasks.ToArray());
        }

        static async Task DeviceToCloudMessage(string deviceId, string deviceConnectionString)
        {
            Console.WriteLine($"{DateTime.Now.ToString("T")}:Start to send msg for {deviceId}");

            var deviceClient = DeviceClient.CreateFromConnectionString(deviceConnectionString, Microsoft.Azure.Devices.Client.TransportType.Mqtt);

            var currentDataValue = 0;
            var messageTemplate = setting.Message;
            if (string.IsNullOrEmpty(messageTemplate))
            {
                messageTemplate = "{\"DeviceData\": %value%,\"DeviceUtcDatetime\": \"%datetime%\"}";
            }

            var stopWatch = Stopwatch.StartNew();
            long lastCheckpoint;
            long interval = 60000 / setting.MessagePerMin;    // in miliseconds

            long expectedNumofMessage = setting.MessagePerMin * setting.DurationInMin;

            long count = 0;
            Random rnd = new Random();
            while (stopWatch.Elapsed.TotalMinutes <= setting.DurationInMin)
            {
                lastCheckpoint = stopWatch.ElapsedMilliseconds;


                currentDataValue = rnd.Next(-20, 20);
                string messageString = string.Empty;
                if (setting.ReadBlobSwitch)
                {
                    IEnumerable<string> messages =
                        messagePereadController.GetMessages(deviceId, DateTime.Now);
                    if (messages != null)
                    {
                        foreach (var message in messages)
                        {
                            await deviceClient.SendEventAsync(new Microsoft.Azure.Devices.Client.Message(Encoding.ASCII.GetBytes(message)));
                            messageString = message;
                            count++;
                        }
                    }
                }
                else
                {
                    messageString = messageTemplate;
                    messageString = messageString.Replace("%deviceId%", deviceId);
                    messageString = messageString.Replace("%value%", currentDataValue.ToString());
                    messageString = messageString.Replace("%datetime%", DateTime.UtcNow.ToString());

                    var message = new Microsoft.Azure.Devices.Client.Message(Encoding.ASCII.GetBytes(messageString));

                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            await deviceClient.SendEventAsync(message);

                            if (count % 10 == 0)
                            {
                                Console.WriteLine($"{DateTime.Now.ToString("T")}:{deviceId} > Sending {count} message");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now.ToString("T")}: {deviceId}: Send message failed: {ex.Message}:{ex.StackTrace}");
                            await Task.Delay(1000);

                            deviceClient = DeviceClient.CreateFromConnectionString(deviceConnectionString, Microsoft.Azure.Devices.Client.TransportType.Mqtt);
                            continue;
                        }

                        count++;
                        break;
                    }

                    //resultUpdater.ReportMessages(deviceId, count);                    
                }

                // add 200ms 
                var delayTime = interval - (stopWatch.ElapsedMilliseconds - lastCheckpoint) - 200;
                if (delayTime > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayTime));
                }
            }
            if (setting.ReadBlobSwitch)
                messagePereadController.StopTransport();

            Console.WriteLine("{0}: {1} - Finish sending {2} message (expected: {3})", stopWatch.Elapsed.ToString(@"mm\:ss"), deviceId, count, expectedNumofMessage);
        }

        static string DeviceConnectionString(string ioTHubHostName, Device device)
        {
            return string.Format("HostName={0};CredentialScope=Device;DeviceId={1};SharedAccessKey={2}",
                ioTHubHostName,
                device.Id,
                device.Authentication.SymmetricKey.PrimaryKey);
        }
    }
}