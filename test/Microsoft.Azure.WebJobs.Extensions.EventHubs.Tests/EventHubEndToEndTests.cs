﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.WebJobs.Host.TestCommon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Host.EndToEndTests
{
    public class EventHubEndToEndTests
    {
        private const string TestHubName = "webjobstesthub";
        private const int Timeout = 30000;
        private static EventWaitHandle _eventWait;
        private static string _testId;
        private static List<string> _results;

        public EventHubEndToEndTests()
        {
            _results = new List<string>();
            _testId = Guid.NewGuid().ToString();
            _eventWait = new ManualResetEvent(initialState: false);
        }

        [Fact]
        public async Task EventHub_PocoBinding()
        {
            var tuple = BuildHost<EventHubTestBindToPocoJobs>();
            using (var host = tuple.Item1)
            {
                var method = typeof(EventHubTestBindToPocoJobs).GetMethod(nameof(EventHubTestBindToPocoJobs.SendEvent_TestHub), BindingFlags.Static | BindingFlags.Public);
                await host.CallAsync(method, new { input = "{ Name: 'foo', Value: '" + _testId +"' }" });

                bool result = _eventWait.WaitOne(Timeout);
                Assert.True(result);

                var logs = tuple.Item2.GetTestLoggerProvider().GetAllLogMessages().Select(p => p.FormattedMessage);

                Assert.Contains($"PocoValues(foo,{_testId})", logs);
            }
        }

        [Fact]
        public async Task EventHub_StringBinding()
        {
            var tuple = BuildHost<EventHubTestBindToStringJobs>();
            using (var host = tuple.Item1)
            {
                var method = typeof(EventHubTestBindToStringJobs).GetMethod(nameof(EventHubTestBindToStringJobs.SendEvent_TestHub), BindingFlags.Static | BindingFlags.Public);
                await host.CallAsync(method, new { input = _testId });

                bool result = _eventWait.WaitOne(Timeout);
                Assert.True(result);

                var logs = tuple.Item2.GetTestLoggerProvider().GetAllLogMessages().Select(p => p.FormattedMessage);

                Assert.Contains($"Input({_testId})", logs);
            }
        }

        [Fact]
        public async Task EventHub_SingleDispatch()
        {
            Tuple<JobHost, IHost> tuple = BuildHost<EventHubTestSingleDispatchJobs>();
            using (var host = tuple.Item1)
            {
                var method = typeof(EventHubTestSingleDispatchJobs).GetMethod(nameof(EventHubTestSingleDispatchJobs.SendEvent_TestHub), BindingFlags.Static | BindingFlags.Public);
                await host.CallAsync(method, new { input = _testId });

                bool result = _eventWait.WaitOne(Timeout);
                Assert.True(result);

                // Wait for checkpointing
                await Task.Delay(3000);

                IEnumerable<LogMessage> logMessages = tuple.Item2.GetTestLoggerProvider()
                    .GetAllLogMessages();

                Assert.Equal(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("Trigger Details:")
                    && x.FormattedMessage.Contains("Offset:")).Count(), 1);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("OpenAsync")).Count() > 0);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("CheckpointAsync")).Count() > 0);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("Sending events to EventHub")).Count() > 0);
            }
        }

        [Fact]
        public async Task EventHub_MultipleDispatch()
        {
            Tuple<JobHost, IHost> tuple = BuildHost<EventHubTestMultipleDispatchJobs>();
            using (var host = tuple.Item1)
            {
                // send some events BEFORE starting the host, to ensure
                // the events are received in batch
                var method = typeof(EventHubTestMultipleDispatchJobs).GetMethod("SendEvents_TestHub", BindingFlags.Static | BindingFlags.Public);
                int numEvents = 5;
                await host.CallAsync(method, new { numEvents = numEvents, input = _testId });

                bool result = _eventWait.WaitOne(Timeout);
                Assert.True(result);

                // Wait for checkpointing
                await Task.Delay(3000);

                IEnumerable<LogMessage> logMessages = tuple.Item2.GetTestLoggerProvider()
                    .GetAllLogMessages();

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("Trigger Details:")
                    && x.FormattedMessage.Contains("Offset:")).Count() > 0);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("OpenAsync")).Count() > 0);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("CheckpointAsync")).Count() > 0);

                Assert.True(logMessages.Where(x => !string.IsNullOrEmpty(x.FormattedMessage)
                    && x.FormattedMessage.Contains("Sending events to EventHub")).Count() > 0);
            }
        }

        [Fact]
        public async Task EventHub_PartitionKey()
        {
            Tuple<JobHost, IHost> tuple = BuildHost<EventHubPartitionKeyTestJobs>();
            using (var host = tuple.Item1)
            {
                var method = typeof(EventHubPartitionKeyTestJobs).GetMethod("SendEvents_TestHub", BindingFlags.Static | BindingFlags.Public);
                _eventWait = new ManualResetEvent(initialState: false);
                await host.CallAsync(method, new { input = _testId });

                bool result = _eventWait.WaitOne(Timeout);

                Assert.True(result);
            }
        }


        public class EventHubTestSingleDispatchJobs
        {
            public static void SendEvent_TestHub(string input, [EventHub(TestHubName)] out EventData evt)
            {
                evt = new EventData(Encoding.UTF8.GetBytes(input));
                evt.Properties.Add("TestProp1", "value1");
                evt.Properties.Add("TestProp2", "value2");
            }

            public static void ProcessSingleEvent([EventHubTrigger(TestHubName)] string evt,
                       string partitionKey, DateTime enqueuedTimeUtc, IDictionary<string, object> properties,
                       IDictionary<string, object> systemProperties)
            {
                // filter for the ID the current test is using
                if (evt == _testId)
                {
                    Assert.True((DateTime.Now - enqueuedTimeUtc).TotalSeconds < 30);

                    Assert.Equal("value1", properties["TestProp1"]);
                    Assert.Equal("value2", properties["TestProp2"]);

                    _eventWait.Set();
                }
            }
        }

        public class EventHubTestBindToPocoJobs
        {
            public static void SendEvent_TestHub(string input, [EventHub(TestHubName)] out EventData evt)
            {
                evt = new EventData(Encoding.UTF8.GetBytes(input));
            }

            public static void BindToPoco([EventHubTrigger(TestHubName)] TestPoco input, string value, string name, ILogger logger)
            {
                if (value == _testId)
                {
                    Assert.Equal(input.Value, value);
                    Assert.Equal(input.Name, name);
                    logger.LogInformation($"PocoValues({name},{value})");
                    _eventWait.Set();
                }
            }
        }

        public class EventHubTestBindToStringJobs
        {
            public static void SendEvent_TestHub(string input, [EventHub(TestHubName)] out EventData evt)
            {
                evt = new EventData(Encoding.UTF8.GetBytes(input));
            }

            public static void BindToString([EventHubTrigger(TestHubName)] string input, ILogger logger)
            {
                if (input == _testId)
                {
                    logger.LogInformation($"Input({input})");
                    _eventWait.Set();
                }
            }
        }

        public class EventHubTestMultipleDispatchJobs
        {
            public static void SendEvents_TestHub(int numEvents, string input, [EventHub(TestHubName)] out EventData[] events)
            {
                events = new EventData[numEvents];
                for (int i = 0; i < numEvents; i++)
                {
                    var evt = new EventData(Encoding.UTF8.GetBytes(input));
                    evt.Properties.Add("TestIndex", i);
                    evt.Properties.Add("TestProp1", "value1");
                    evt.Properties.Add("TestProp2", "value2");
                    events[i] = evt;
                }
            }

            public static void ProcessMultipleEvents([EventHubTrigger(TestHubName)] string[] events,
                string[] partitionKeyArray, DateTime[] enqueuedTimeUtcArray, IDictionary<string, object>[] propertiesArray,
                IDictionary<string, object>[] systemPropertiesArray)
            {
                Assert.Equal(events.Length, partitionKeyArray.Length);
                Assert.Equal(events.Length, enqueuedTimeUtcArray.Length);
                Assert.Equal(events.Length, propertiesArray.Length);
                Assert.Equal(events.Length, systemPropertiesArray.Length);

                for (int i = 0; i < events.Length; i++)
                {
                    Assert.Equal(i, propertiesArray[i]["TestIndex"]);
                }

                // filter for the ID the current test is using
                if (events[0] == _testId)
                {
                    _results.AddRange(events);
                    _eventWait.Set();
                }
            }
        }

        public class EventHubPartitionKeyTestJobs
        {
            public static async Task SendEvents_TestHub(
                string input,
                [EventHub(TestHubName)] EventHubClient client)
            {
                List<EventData> list = new List<EventData>();
                EventData evt = new EventData(Encoding.UTF8.GetBytes(input));

                // Send event without PK
                await client.SendAsync(evt);

                // Send event with different PKs
                for (int i = 0; i < 5; i++)
                {
                    evt = new EventData(Encoding.UTF8.GetBytes(input));
                    await client.SendAsync(evt, "test_pk" + i);
                }
            }

            public static void ProcessMultiplePartitionEvents([EventHubTrigger(TestHubName)] EventData[] events)
            {
                foreach (EventData eventData in events)
                {
                    string message = Encoding.UTF8.GetString(eventData.Body);

                    // filter for the ID the current test is using
                    if (message == _testId)
                    {
                        _results.Add(eventData.SystemProperties.PartitionKey);
                        _results.Sort();

                        if (_results.Count == 6 && _results[5] == "test_pk4")
                        {
                            _eventWait.Set();
                        }
                    }
                }
            }
        }

        private Tuple<JobHost, IHost> BuildHost<T>()
        {
            JobHost jobHost = null;

            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddTestSettings()
                .Build();

            const string connectionName = "AzureWebJobsTestHubConnection";
            string connection = config.GetConnectionStringOrSetting(connectionName);
            Assert.True(!string.IsNullOrEmpty(connection), $"Required test connection string '{connectionName}' is missing.");

            IHost host = new HostBuilder()
                .ConfigureDefaultTestHost<T>(b =>
                {
                    b.AddEventHubs(options =>
                    {
                        options.EventProcessorOptions.EnableReceiverRuntimeMetric = true;
                        options.AddSender(TestHubName, connection);
                        options.AddReceiver(TestHubName, connection);
                    });
                })
                .ConfigureLogging(b =>
                {
                    b.SetMinimumLevel(LogLevel.Debug);
                })
                .Build();

            jobHost = host.GetJobHost();
            jobHost.StartAsync().GetAwaiter().GetResult();

            return new Tuple<JobHost, IHost>(jobHost, host);
        }
        public class TestPoco
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }
    }
}