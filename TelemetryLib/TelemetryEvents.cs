// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;

namespace FabricObserver.TelemetryLib
{
    /// <summary>
    /// Contains common FabricObserver telemetry events
    /// </summary>
    public class TelemetryEvents : IDisposable
    {
        private const string OperationalEventName = "OperationalEvent";
        private const string CriticalErrorEventName = "CriticalErrorEvent";
        private const string TaskName = "FabricObserver";
        private readonly TelemetryClient telemetryClient;
        private readonly ServiceContext serviceContext;
        private readonly ITelemetryEventSource serviceEventSource;
        private readonly string clusterId, tenantId, clusterType;
        private readonly TelemetryConfiguration appInsightsTelemetryConf;
        private readonly bool isEtwEnabled;

        public TelemetryEvents(
                    FabricClient fabricClient,
                    ServiceContext context,
                    ITelemetryEventSource eventSource,
                    CancellationToken token,
                    bool etwEnabled)
        {
            serviceEventSource = eventSource;
            serviceContext = context;
            string config = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "FOAppInsightsOperational.config"));
            appInsightsTelemetryConf = TelemetryConfiguration.CreateFromConfiguration(config);
            appInsightsTelemetryConf.InstrumentationKey = TelemetryConstants.AIKey;
            telemetryClient = new TelemetryClient(appInsightsTelemetryConf);
            var (ClusterId, TenantId, ClusterType) = ClusterIdentificationUtility.TupleGetClusterIdAndTypeAsync(fabricClient, token).GetAwaiter().GetResult();
            clusterId = ClusterId;
            tenantId = TenantId;
            clusterType = ClusterType;
            isEtwEnabled = etwEnabled;
        }

        public bool EmitFabricObserverOperationalEvent(FabricObserverOperationalEventData foData, TimeSpan runInterval, string logFilePath)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                // ETW
                if (isEtwEnabled)
                {
                    serviceEventSource.InternalFODataEvent(new { FOInternalTelemtryData = JsonConvert.SerializeObject(foData) });
                }

                string nodeHashString = string.Empty;
                int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

                if (nodeNameHash != -1)
                {
                    nodeHashString = ((uint)nodeNameHash).ToString();
                }

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", OperationalEventName},
                    { "TaskName", TaskName},
                    { "EventRunInterval", runInterval.ToString() },
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "NodeNameHash", nodeHashString },
                    { "FOVersion", foData.Version },
                    { "HasPlugins", foData.HasPlugins.ToString() },
                    { "ParallelExecution", foData.ParallelExecutionEnabled.ToString() },
                    { "UpTime", foData.UpTime },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", foData.OS }
                };

                if (eventProperties.TryGetValue("ClusterType", out string clustType))
                {
                    if (clustType != TelemetryConstants.ClusterTypeSfrp)
                    {
                        eventProperties.Add("TenantId", tenantId);
                    }
                }

                IDictionary<string, double> metrics = new Dictionary<string, double>
                {
                    { "EnabledObserverCount", foData.EnabledObserverCount }
                };

                const string err = "ErrorDetections";
                const string warn = "WarningDetections";
                const string apps = "TotalMonitoredApps";
                const string procs = "TotalMonitoredServiceProcesses";
                const string conts = "TotalMonitoredContainers";

                foreach (var obData in foData.ObserverData)
                {
                    double data = 0;
                    string key;

                    // These observers monitor app services/containers.
                    if (obData.ObserverName.Contains("AppObserver") || obData.ObserverName.Contains("FabricSystemObserver")
                        || obData.ObserverName.Contains("NetworkObserver") || obData.ObserverName.Contains("ContainerObserver"))
                    {
                        // App count.
                        data = ((AppServiceObserverData)obData).MonitoredAppCount;
                        key = $"{obData.ObserverName}{apps}";
                        metrics.Add(key, data);

                        // Process (service instance/primary replica/container) count.
                        data = ((AppServiceObserverData)obData).MonitoredServiceProcessCount;
                        key = $"{obData.ObserverName}{procs}";

                        if (obData.ObserverName.Contains("ContainerObserver"))
                        {
                            key = $"{obData.ObserverName}{conts}";
                        }

                        metrics.Add(key, data);
                    }

                    // AzureStorage and SFConfig observers do not generate health events.
                    if (obData.ObserverName.Contains("AzureStorageUploadObserver") || obData.ObserverName.Contains("SFConfigurationObserver"))
                    {
                        key = $"{obData.ObserverName}Enabled";
                        data = 1; // Enabled.
                        metrics.Add(key, data);
                    }
                    else
                    {
                        // Observer-created Error count
                        key = $"{obData.ObserverName}{err}";
                        data = obData.ErrorCount;
                        metrics.Add(key, data);

                        // Observer-created Warning count
                        key = $"{obData.ObserverName}{warn}";
                        data = obData.WarningCount;
                        metrics.Add(key, data);
                    }
                }

                telemetryClient?.TrackEvent($"{TaskName}.{OperationalEventName}", eventProperties, metrics);
                telemetryClient?.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                // write a local log file containing the exact information sent to MS \\
                string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray());
                telemetryData += "," + string.Join(",", metrics.Select(kv => $"\"{kv.Key}\":" + kv.Value).ToArray()) + "}";
                _ = TryWriteLogFile(logFilePath, telemetryData);

                eventProperties.Clear();
                eventProperties = null;
                metrics.Clear();
                metrics = null;

                return true;
            }
            catch (Exception e)
            {
                // Telemetry is non-critical and should not take down FO.
                _ = TryWriteLogFile(logFilePath, $"{e}");
            }

            return false;
        }

        public bool EmitFabricObserverCriticalErrorEvent(FabricObserverCriticalErrorEventData foErrorData, string logFilePath)
        {
            if (!telemetryClient.IsEnabled())
            {
                return false;
            }

            try
            {
                // ETW
                if (isEtwEnabled)
                {
                    serviceEventSource.InternalFOCriticalErrorDataEvent(new { FOCriticalErrorData = JsonConvert.SerializeObject(foErrorData) });
                }

                string nodeHashString = string.Empty;
                int nodeNameHash = serviceContext?.NodeContext.NodeName.GetHashCode() ?? -1;

                if (nodeNameHash != -1)
                {
                    nodeHashString = ((uint)nodeNameHash).ToString();
                }

                IDictionary<string, string> eventProperties = new Dictionary<string, string>
                {
                    { "EventName", CriticalErrorEventName},
                    { "TaskName", TaskName},
                    { "ClusterId", clusterId },
                    { "ClusterType", clusterType },
                    { "TenantId", tenantId },
                    { "NodeNameHash",  nodeHashString },
                    { "FOVersion", foErrorData.Version },
                    { "CrashTime", foErrorData.CrashTime },
                    { "ErrorMessage", foErrorData.ErrorMessage },
                    { "CrashData", foErrorData.ErrorStack },
                    { "Timestamp", DateTime.UtcNow.ToString("o") },
                    { "OS", foErrorData.OS }
                };

                telemetryClient?.TrackEvent($"{TaskName}.{OperationalEventName}", eventProperties);
                telemetryClient?.Flush();

                // allow time for flushing
                Thread.Sleep(1000);

                // write a local log file containing the exact information sent to MS \\
                string telemetryData = "{" + string.Join(",", eventProperties.Select(kv => $"\"{kv.Key}\":" + $"\"{kv.Value}\"").ToArray()) + "}";
                _ = TryWriteLogFile(logFilePath, telemetryData);

                return true;
            }
            catch
            {
                // Telemetry is non-critical and should not take down FO.
            }

            return false;
        }

        public void Dispose()
        {
            telemetryClient?.Flush();

            // allow time for flushing.
            Thread.Sleep(1000);
            appInsightsTelemetryConf?.Dispose();
        }

        const int Retries = 4;

        private bool TryWriteLogFile(string path, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            for (var i = 0; i < Retries; i++)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);

                    if (!Directory.Exists(directory))
                    {
                        if (directory != null)
                        {
                            _ = Directory.CreateDirectory(directory);
                        }
                    }

                    File.WriteAllText(path, content);
                    return true;
                }
                catch
                {

                }

                Thread.Sleep(1000);
            }

            return false;
        }
    }
}