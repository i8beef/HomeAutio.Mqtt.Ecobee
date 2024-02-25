using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Options;
using HomeAutio.Mqtt.Ecobee.Options;
using I8Beef.Ecobee;
using I8Beef.Ecobee.Protocol;
using I8Beef.Ecobee.Protocol.Functions;
using I8Beef.Ecobee.Protocol.Objects;
using I8Beef.Ecobee.Protocol.Thermostat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using Newtonsoft.Json;

namespace HomeAutio.Mqtt.Ecobee
{
    /// <summary>
    /// Ecobee MQTT Topshelf service class.
    /// </summary>
    public class EcobeeMqttService : ServiceBase
    {
        private readonly ILogger<EcobeeMqttService> _log;

        private readonly Client _client;

        private readonly Dictionary<string, RevisionStatus> _revisionStatusCache;
        private readonly Dictionary<string, ThermostatStatus> _thermostatStatus;
        private readonly int _refreshInterval;

        private bool _disposed;
        private System.Timers.Timer? _refresh;

        /// <summary>
        /// Initializes a new instance of the <see cref="EcobeeMqttService"/> class.
        /// </summary>
        /// <param name="logger">Logging instance.</param>
        /// <param name="ecobeeClient">The Ecobee client.</param>
        /// <param name="ecobeeOptions">The Evobee options.</param>
        /// <param name="mqttOptions">MQTT broker options.</param>
        public EcobeeMqttService(
            ILogger<EcobeeMqttService> logger,
            Client ecobeeClient,
            IOptions<EcobeeOptions> ecobeeOptions,
            IOptions<MqttOptions> mqttOptions)
            : base(logger, mqttOptions, "ecobee/" + ecobeeOptions.Value.EcobeeName)
        {
            _log = logger;
            _refreshInterval = ecobeeOptions.Value.RefreshInterval * 1000;
            SubscribedTopics.Add(TopicRoot + "/+/+/set");

            _client = ecobeeClient;
            _revisionStatusCache = new Dictionary<string, RevisionStatus>();
            _thermostatStatus = new Dictionary<string, ThermostatStatus>();
        }

        #region Service implementation

        /// <inheritdoc />
        protected override async Task StartServiceAsync(CancellationToken cancellationToken = default)
        {
            await GetInitialStatusAsync()
                .ConfigureAwait(false);

            // Enable refresh
            _refresh?.Dispose();
            _refresh = new System.Timers.Timer();
            _refresh.Elapsed += async (sender, e) => await RefreshAsync();
            _refresh.Interval = _refreshInterval;
            _refresh.Start();
        }

        /// <inheritdoc />
        protected override Task StopServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Harmony published to MQTT.
        /// </summary>
        /// <param name="e">Event args.</param>
        protected override async Task MqttMsgPublishReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            var message = e.ApplicationMessage.ConvertPayloadToString();
            _log.LogInformation("MQTT message received for topic " + e.ApplicationMessage.Topic + ": " + message);

            // Parse topic out
            var topicWithoutRoot = e.ApplicationMessage.Topic.Substring(TopicRoot.Length + 1);
            var thermostatId = topicWithoutRoot.Substring(0, topicWithoutRoot.IndexOf('/'));
            var thermostatTopic = topicWithoutRoot.Substring(topicWithoutRoot.IndexOf('/') + 1);

            // ONLY VALID OPTIONS
            // HvacMode (auto, auxHeatOnly, cool, heat, off) - hvacMode/set
            // Fan (on, off, auto) - desiredFanMode/set
            // SetHold ({ hold object }) - hold/set
            // Desired Heat - desiredHeat/set
            // Desired Cool - desiredCool/set
            var request = new ThermostatUpdateRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostats",
                    SelectionMatch = thermostatId
                }
            };

            _log.LogInformation($"Sending request to {request.Uri} with thermostat selection {thermostatId} for action {thermostatTopic}");

            switch (thermostatTopic)
            {
                case "desiredCool/set":
                    if (int.TryParse(message, out var desiredCoolValue)
                        && _thermostatStatus.TryGetValue(thermostatId, out var desiredCoolThermostatStatus)
                        && decimal.TryParse(desiredCoolThermostatStatus.Status["desiredHeat"], out var currentDesiredHeatValue))
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    CoolHoldTemp = desiredCoolValue * 10,
                                    HeatHoldTemp = (int)Math.Round(currentDesiredHeatValue * 10, MidpointRounding.AwayFromZero)
                                }
                            }
                        };
                        var desiredCoolResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredCoolResponse.Status.Code}) {desiredCoolResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "desiredFanMode/set":
                    if (message is "auto" or "off" or "on")
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    Fan = message
                                }
                            }
                        };

                        var desiredFanModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredFanModeResponse.Status.Code}) {desiredFanModeResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "desiredHeat/set":
                    if (int.TryParse(message, out var desiredHeatValue)
                        && _thermostatStatus.TryGetValue(thermostatId, out var desiredHeatThermostatStatus)
                        && decimal.TryParse(desiredHeatThermostatStatus.Status["desiredCool"], out var currentDesiredCoolValue))
                    {
                        request.Functions = new List<Function>
                        {
                            new SetHoldFunction
                            {
                                Params = new SetHoldParams
                                {
                                    HoldType = "nextTransition",
                                    CoolHoldTemp = (int)Math.Round(currentDesiredCoolValue * 10, MidpointRounding.AwayFromZero),
                                    HeatHoldTemp = desiredHeatValue * 10
                                }
                            }
                        };

                        var desiredHeatResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({desiredHeatResponse.Status.Code}) {desiredHeatResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                case "hold/set":
                    try
                    {
                        if (message.Contains("resumeProgram"))
                        {
                            var resumeFunction = JsonSerializer<ResumeProgramFunction>.Deserialize(message);
                            request.Functions = new List<Function>
                            {
                                resumeFunction
                            };
                        }
                        else
                        {
                            var holdFunction = JsonSerializer<SetHoldFunction>.Deserialize(message);
                            request.Functions = new List<Function>
                            {
                                holdFunction
                            };
                        }

                        var setHoldResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({setHoldResponse.Status.Code}) {setHoldResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }
                    catch (JsonException ex)
                    {
                        _log.LogWarning(ex, $"Could not deserialize payload: {message}");
                    }

                    break;
                case "hvacMode/set":
                    if (message is "auto" or "auxHeatOnly" or "cool" or "heat" or "off")
                    {
                        request.Thermostat = new { Settings = new { HvacMode = message } };
                        var hvacModeResponse = await _client.PostAsync<ThermostatUpdateRequest, Response>(request)
                            .ConfigureAwait(false);
                        _log.LogInformation($"{request.Uri} response: ({hvacModeResponse.Status.Code}) {hvacModeResponse.Status.Message}");

                        // Publish updates and cache new values
                        await GetInitialStatusAsync();
                    }

                    break;
                default:
                    _log.LogWarning($"Unknown command topic {thermostatTopic} for thermostat {thermostatId}");
                    break;
            }
        }

        #endregion

        #region Ecobee implementation

        /// <summary>
        /// Heartbeat ping. Failure will result in the heartbeat being stopped, which will
        /// make any future calls throw an exception as the heartbeat indicator will be disabled.
        /// </summary>
        /// <returns>>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task RefreshAsync()
        {
            // Get current revision status
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var status = await _client.GetAsync<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest);
            foreach (var thermostatRevision in status.RevisionList)
            {
                _log.LogInformation($"Got revision: {thermostatRevision}");
                var revisionStatus = new RevisionStatus(thermostatRevision);

                if (_revisionStatusCache[revisionStatus.ThermostatIdentifier].ThermostatRevision != revisionStatus.ThermostatRevision ||
                    _revisionStatusCache[revisionStatus.ThermostatIdentifier].RuntimeRevision != revisionStatus.RuntimeRevision)
                {
                    await RefreshThermostatAsync(revisionStatus);
                }

                // Cache last run values
                _revisionStatusCache[revisionStatus.ThermostatIdentifier] = revisionStatus;
            }
        }

        /// <summary>
        /// Get, cache, and publish initial states.
        /// </summary>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private async Task GetInitialStatusAsync()
        {
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var summary = await _client.GetAsync<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest)
                .ConfigureAwait(false);

            // Set initial revision cache
            _revisionStatusCache.Clear();
            _thermostatStatus.Clear();
            foreach (var revision in summary.RevisionList)
            {
                _log.LogInformation($"Got revision: {revision}");
                var revisionStatus = new RevisionStatus(revision);

                await RefreshThermostatAsync(revisionStatus);
                _revisionStatusCache.Add(revisionStatus.ThermostatIdentifier, revisionStatus);
            }
        }

        /// <summary>
        /// Handles updating status for a thermostat and publishing changes.
        /// </summary>
        /// <param name="revisionStatus">Revision status that triggered the update.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task RefreshThermostatAsync(RevisionStatus revisionStatus)
        {
            // Build single thermostat request
            var thermostatRequest = new ThermostatRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostats",
                    SelectionMatch = revisionStatus.ThermostatIdentifier,
                    IncludeEquipmentStatus = true,
                    IncludeEvents = true,
                    IncludeRuntime = true,
                    IncludeSensors = true,
                    IncludeSettings = true,
                    IncludeWeather = true
                }
            };

            var thermostatUpdate = await _client.GetAsync<ThermostatRequest, ThermostatResponse>(thermostatRequest)
                .ConfigureAwait(false);

            // Publish updates and cache new values
            await UpdateState(thermostatUpdate?.ThermostatList?.FirstOrDefault());
        }

        /// <summary>
        /// Update state and publish new statuses.
        /// </summary>
        /// <param name="thermostat">Thermostat status.</param>
        /// <returns>>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task UpdateState(Thermostat? thermostat)
        {
            if (thermostat == null)
            {
                return;
            }

            var thermostatStatus = new ThermostatStatus();

            // Equipment status
            foreach (var device in thermostat.EquipmentStatus.Split(',').Where(x => !string.IsNullOrEmpty(x)))
            {
                thermostatStatus.EquipmentStatus[device] = "on";
            }

            // Status
            thermostatStatus.Status["hvacMode"] = thermostat.Settings.HvacMode;
            thermostatStatus.Status["humidifierMode"] = thermostat.Settings.HumidifierMode;
            thermostatStatus.Status["dehumidifierMode"] = thermostat.Settings.DehumidifierMode;
            thermostatStatus.Status["autoAway"] = thermostat.Settings.AutoAway.HasValue && thermostat.Settings.AutoAway.Value ? "true" : "false";
            thermostatStatus.Status["vent"] = thermostat.Settings.Vent;
            thermostatStatus.Status["actualTemperature"] = thermostat.Runtime.ActualTemperature.HasValue ? (thermostat.Runtime.ActualTemperature.Value / 10m).ToString() : null;
            thermostatStatus.Status["actualHumidity"] = thermostat.Runtime.ActualHumidity.HasValue ? thermostat.Runtime.ActualHumidity.Value.ToString() : null;
            thermostatStatus.Status["desiredHeat"] = thermostat.Runtime.DesiredHeat.HasValue ? (thermostat.Runtime.DesiredHeat.Value / 10m).ToString() : null;
            thermostatStatus.Status["desiredCool"] = thermostat.Runtime.DesiredCool.HasValue ? (thermostat.Runtime.DesiredCool.Value / 10m).ToString() : null;
            thermostatStatus.Status["desiredHumidity"] = thermostat.Runtime.DesiredHumidity.HasValue ? thermostat.Runtime.DesiredHumidity.Value.ToString() : null;
            thermostatStatus.Status["desiredDehumidity"] = thermostat.Runtime.DesiredDehumidity.HasValue ? thermostat.Runtime.DesiredDehumidity.Value.ToString() : null;
            thermostatStatus.Status["desiredFanMode"] = thermostat.Runtime.DesiredFanMode;

            // Weather forcasts
            var forecast = thermostat.Weather.Forecasts?.FirstOrDefault();
            if (forecast != null)
            {
                thermostatStatus.Status["weatherDewPoint"] = forecast.Dewpoint.HasValue ? (forecast.Dewpoint.Value / 10m).ToString() : null;
                thermostatStatus.Status["weatherPrecipitationChance"] = forecast.Pop.HasValue ? forecast.Pop.Value.ToString() : null;
                thermostatStatus.Status["weatherPressure"] = forecast.Pressure.HasValue ? (forecast.Pressure.Value / 100m).ToString() : null;
                thermostatStatus.Status["weatherRelativeHumidity"] = forecast.RelativeHumidity.HasValue ? forecast.RelativeHumidity.Value.ToString() : null;
                thermostatStatus.Status["weatherTemperature"] = forecast.Temperature.HasValue ? (forecast.Temperature.Value / 10m).ToString() : null;
                thermostatStatus.Status["weatherTempLow"] = forecast.TempLow.HasValue ? (forecast.TempLow.Value / 10m).ToString() : null;
                thermostatStatus.Status["weatherTempHigh"] = forecast.TempHigh.HasValue ? (forecast.TempHigh.Value / 10m).ToString() : null;
                thermostatStatus.Status["weatherVisibility"] = forecast.Visibility.HasValue ? forecast.Visibility.Value.ToString() : null;
                thermostatStatus.Status["weatherWindBearing"] = forecast.WindBearing.HasValue ? forecast.WindBearing.Value.ToString() : null;
                thermostatStatus.Status["weatherWindDirection"] = forecast.WindDirection;
                thermostatStatus.Status["weatherWindGust"] = forecast.WindGust.HasValue ? forecast.WindGust.Value.ToString() : null;
                thermostatStatus.Status["weatherWindSpeed"] = forecast.WindSpeed.HasValue ? forecast.WindSpeed.Value.ToString() : null;
            }

            // Sensors
            if (thermostat.RemoteSensors != null && thermostat.RemoteSensors.Count > 0)
            {
                foreach (var sensor in thermostat.RemoteSensors)
                {
                    thermostatStatus.Sensors[sensor.Name] = sensor.Capability.ToDictionary(
                        s => s.Type,
                        s =>
                        {
                            // Convert temperature values to human readable
                            if (string.Equals(s.Type, "temperature", StringComparison.OrdinalIgnoreCase))
                            {
                                if (decimal.TryParse(s.Value, out var decimalValue))
                                {
                                    return (decimalValue / 10m).ToString();
                                }
                            }

                            return s.Value;
                        })!;
                }
            }

            // Hold
            var holdEvent = thermostat.Events.FirstOrDefault(x => x.Type == "hold");
            if (holdEvent != null && holdEvent.Running.HasValue && holdEvent.Running.Value)
            {
                thermostatStatus.ActiveHold["running"] = holdEvent.Running.HasValue && holdEvent.Running.Value ? "true" : "false";
                thermostatStatus.ActiveHold["startTime"] = DateTime.TryParse($"{holdEvent.StartDate} {holdEvent.StartTime}", out var startTimeResult) ? startTimeResult.ToString() : null;
                thermostatStatus.ActiveHold["endTime"] = DateTime.TryParse($"{holdEvent.EndDate} {holdEvent.EndTime}", out var endTimeResult) ? endTimeResult.ToString() : null;
                thermostatStatus.ActiveHold["coldHoldTemp"] = holdEvent.CoolHoldTemp.HasValue ? (holdEvent.CoolHoldTemp.Value / 10m).ToString() : null;
                thermostatStatus.ActiveHold["heatHoldTemp"] = holdEvent.HeatHoldTemp.HasValue ? (holdEvent.HeatHoldTemp.Value / 10m).ToString() : null;
                thermostatStatus.ActiveHold["fan"] = holdEvent.Fan;
                thermostatStatus.ActiveHold["fanMinOnTime"] = holdEvent.FanMinOnTime.HasValue ? holdEvent.FanMinOnTime.Value.ToString() : null;
                thermostatStatus.ActiveHold["vent"] = holdEvent.Vent;
                thermostatStatus.ActiveHold["ventilatorMinOnTime"] = holdEvent.VentilatorMinOnTime.HasValue ? holdEvent.VentilatorMinOnTime.Value.ToString() : null;
            }

            if (_thermostatStatus.TryGetValue(thermostat.Identifier, out var currentThermostatStatus))
            {
                // Publish updates
                foreach (var device in thermostatStatus.EquipmentStatus)
                {
                    if (device.Value != currentThermostatStatus.EquipmentStatus[device.Key] &&
                        device.Value != null)
                    {
                        await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{device.Key}")
                            .WithPayload(device.Value)
                            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                foreach (var status in thermostatStatus.Status)
                {
                    if (status.Value != currentThermostatStatus.Status[status.Key] &&
                        status.Value != null)
                    {
                        await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{status.Key}")
                            .WithPayload(status.Value)
                            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                // Hold status
                foreach (var holdStatus in thermostatStatus.ActiveHold)
                {
                    if (holdStatus.Value != currentThermostatStatus.ActiveHold[holdStatus.Key] &&
                        holdStatus.Value != null)
                    {
                        await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/hold/{holdStatus.Key}")
                            .WithPayload(holdStatus.Value)
                            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }

                // Publish everything for new sensors, new capabilities, and changes in existing ability values
                foreach (var sensor in thermostatStatus.Sensors)
                {
                    foreach (var sensorCapability in sensor.Value)
                    {
                        if (!currentThermostatStatus.Sensors.ContainsKey(sensor.Key) &&
                            sensorCapability.Value != null)
                        {
                            await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                        else if (!currentThermostatStatus.Sensors[sensor.Key].ContainsKey(sensorCapability.Key) &&
                            sensorCapability.Value != null)
                        {
                            await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                        else if (sensorCapability.Value != currentThermostatStatus.Sensors[sensor.Key][sensorCapability.Key] &&
                            sensorCapability.Value != null)
                        {
                            await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                                .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                                .WithPayload(sensorCapability.Value)
                                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                                .WithRetainFlag()
                                .Build())
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                // Publish initial state
                foreach (var device in thermostatStatus.EquipmentStatus.Where(x => x.Value != null))
                {
                    await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{device.Key}")
                        .WithPayload(device.Value)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                foreach (var status in thermostatStatus.Status.Where(x => x.Value != null))
                {
                    await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"{TopicRoot}/{thermostat.Identifier}/{status.Key}")
                        .WithPayload(status.Value)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                // Hold status
                foreach (var holdStatus in thermostatStatus.ActiveHold.Where(x => x.Value != null))
                {
                    await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"{TopicRoot}/{thermostat.Identifier}/hold/{holdStatus.Key}")
                        .WithPayload(holdStatus.Value)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag()
                        .Build())
                        .ConfigureAwait(false);
                }

                foreach (var sensor in thermostatStatus.Sensors)
                {
                    foreach (var sensorCapability in sensor.Value.Where(x => x.Value != null))
                    {
                        await MqttClient.EnqueueAsync(new MqttApplicationMessageBuilder()
                            .WithTopic($"{TopicRoot}/{thermostat.Identifier}/sensor/{sensor.Key.Sluggify()}/{sensorCapability.Key}")
                            .WithPayload(sensorCapability.Value)
                            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                            .WithRetainFlag()
                            .Build())
                            .ConfigureAwait(false);
                    }
                }
            }

            _thermostatStatus[thermostat.Identifier] = thermostatStatus;
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Dispose implementation.
        /// </summary>
        /// <param name="disposing">Indicates if disposing.</param>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                if (_refresh != null)
                {
                    _refresh.Stop();
                    _refresh.Dispose();
                }
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }
}
