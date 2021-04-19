/*
  This demo application accompanies Pluralsight course 'Microsoft Azure IoT Developer: Manage IoT Devices with Azure IoT Central', 
  by Jurgen Kevelaers. See https://pluralsight.pxf.io/iot-central.

  MIT License

  Copyright (c) 2021 Jurgen Kevelaers

  Permission is hereby granted, free of charge, to any person obtaining a copy
  of this software and associated documentation files (the "Software"), to deal
  in the Software without restriction, including without limitation the rights
  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
  copies of the Software, and to permit persons to whom the Software is
  furnished to do so, subject to the following conditions:

  The above copyright notice and this permission notice shall be included in all
  copies or substantial portions of the Software.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
  SOFTWARE.
*/

using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iot_developer_devices_iot_central_m2
{
  class Program
  {
    // TODO: set your IoT Central settings here
    private const string provisioningGlobalDeviceEndpoint = "global.azure-devices-provisioning.net";
    private const string provisioningIdScope = "TODO";
    private const string deviceId = "room-device-01";
    private const string devicePrimaryKey = "TODO";

    private const string CleanSystemDirectMethodName = "CleanSystem";

    private const string heatingClimateControlState = "Heating";
    private const string coolingClimateControlState = "Cooling";
    private const string idleClimateControlState = "Idle";

    private const double minimumHumidity = 30.0;
    private const double maximumHumidity = 80.0;

    private static readonly ConsoleColor defaultConsoleForegroundColor = Console.ForegroundColor;
    private static readonly object lockObject = new object();
    private static readonly Random random = new Random();

    private static double currentHumidity = 45;
    private static double currentTemperature = 68.2;
    private static double targetTemperature = currentTemperature;
    private static string currentClimateControlState = idleClimateControlState;

    static async Task Main(string[] args)
    {
      ConsoleWriteLine("*** Press ENTER to start ***");
      Console.ReadLine();

      ConsoleWriteLine("*** Starting... ***");
      ConsoleWriteLine("*** Press ENTER to quit ***");
      ConsoleWriteLine();

      await Task.Delay(1000);

      var deviceRegistrationResult = await RegisterDevice();
      if (deviceRegistrationResult == null)
      {
        return;
      }

      using var deviceClient = NewDeviceClient(deviceRegistrationResult.AssignedHub);
      if (deviceClient == null)
      {
        return;
      }

      await ReadDesiredPropertiesFromTwin(deviceClient);
      await StartListeningForDirectMethod(deviceClient);
      await StartListeningForDesiredPropertyChanges(deviceClient);
      await SendReportedProperties(deviceClient);

      using var cancellationTokenSource = new CancellationTokenSource();
      var cancellationToken = cancellationTokenSource.Token;

      var sendDeviceDataTask = SendDeviceDataUntilCancelled(deviceClient, cancellationToken);

      Console.ReadLine();
      ConsoleWriteLine("Shutting down...");

      cancellationTokenSource.Cancel(); // request cancel
      sendDeviceDataTask.Wait(); // wait for cancel
    }

    #region Provisioning

    private static async Task<DeviceRegistrationResult> RegisterDevice()
    {
      try
      {
        ConsoleWriteLine($"Will register device {deviceId}...", ConsoleColor.White);

        // using symmetric keys
        using var securityProvider = new SecurityProviderSymmetricKey(
          registrationId: deviceId,
          primaryKey: devicePrimaryKey,
          secondaryKey: null);

        using var transportHandler = new ProvisioningTransportHandlerMqtt(TransportFallbackType.TcpOnly);

        // set up provisioning client for given device
        var provisioningDeviceClient = ProvisioningDeviceClient.Create(
          globalDeviceEndpoint: provisioningGlobalDeviceEndpoint,
          idScope: provisioningIdScope,
          securityProvider: securityProvider,
          transport: transportHandler);

        // register device
        var deviceRegistrationResult = await provisioningDeviceClient.RegisterAsync();

        ConsoleWriteLine($"Device {deviceId} registration result: {deviceRegistrationResult.Status}", ConsoleColor.White);

        if (deviceRegistrationResult.Status != ProvisioningRegistrationStatusType.Assigned)
        {
          throw new Exception($"Failed to register device {deviceId}");
        }

        ConsoleWriteLine($"Device {deviceId} was assigned to hub '{deviceRegistrationResult.AssignedHub}'", ConsoleColor.White);
        ConsoleWriteLine();

        return deviceRegistrationResult;
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }

      return null;
    }

    #endregion

    #region DeviceClient

    private static DeviceClient NewDeviceClient(string assignedHub)
    {
      try
      {
        ConsoleWriteLine();
        ConsoleWriteLine($"Will create client for device {deviceId}...", ConsoleColor.Green);

        var authenticationMethod = new DeviceAuthenticationWithRegistrySymmetricKey(
          deviceId: deviceId, 
          key: devicePrimaryKey);

        var deviceClient = DeviceClient.Create(
           hostname: assignedHub,
           authenticationMethod: authenticationMethod,
           transportType: TransportType.Mqtt_Tcp_Only);

        ConsoleWriteLine($"Successfully created client for device {deviceId}", ConsoleColor.Green);

        return deviceClient;
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }

      return null;
    }

    private static async Task ReadDesiredPropertiesFromTwin(DeviceClient deviceClient)
    {
      try
      {
        ConsoleWriteLine();
        ConsoleWriteLine($"Will get twin for device {deviceId}...", ConsoleColor.Green);

        var twin = await deviceClient.GetTwinAsync();
        
        ConsoleWriteLine($"Successfully got twin for for device {deviceId}:", ConsoleColor.Green);
        ConsoleWriteLine(twin.ToJson(Formatting.Indented), ConsoleColor.Green);

        ApplyDesiredProperties(twin.Properties.Desired);
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static void ApplyDesiredProperties(TwinCollection desiredProperties)
    {
      try
      {
        if (desiredProperties == null)
        {
          return;
        }

        if (desiredProperties.Contains("TargetTemperature") 
          && desiredProperties["TargetTemperature"] != null)
        {
          // update our target temperature with the desired one
          targetTemperature = desiredProperties["TargetTemperature"];

          // do we need to cool or heat?
          if (currentTemperature >= targetTemperature)
          {
            currentClimateControlState = coolingClimateControlState;
          }
          else if (currentTemperature <= targetTemperature)
          {
            currentClimateControlState = heatingClimateControlState;
          }
        }
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static async Task StartListeningForDirectMethod(DeviceClient deviceClient)
    {
      try
      {
        ConsoleWriteLine();
        ConsoleWriteLine($"Will setup listener for direct method {CleanSystemDirectMethodName}...", ConsoleColor.Green);

        // attach handler for direct method
        await deviceClient.SetMethodHandlerAsync(
          methodName: CleanSystemDirectMethodName,
          methodHandler: CleanSystemDirectMethodCallback,
          userContext: deviceClient);

        ConsoleWriteLine($"Now listening for direct method {CleanSystemDirectMethodName}", ConsoleColor.Green);
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static async Task<MethodResponse> CleanSystemDirectMethodCallback(MethodRequest methodRequest, object userContext)
    {
      try
      {
        var deviceClient = (DeviceClient)userContext;

        ConsoleWriteLine();
        ConsoleWriteLine($"Direct method {methodRequest.Name} was invoked", ConsoleColor.Magenta);

        // cleaning mode should be in method payload
        var cleaningMode = methodRequest.Data == null 
          ? null
          : Encoding.UTF8.GetString(methodRequest.Data);

        if (string.IsNullOrEmpty(cleaningMode))
        {
          throw new Exception($"Missing payload for direct method {methodRequest.Name}");
        }

        ConsoleWriteLine($"Cleaning system now (mode: {cleaningMode})...", ConsoleColor.Magenta);

        // simulate cleaning by waiting a bit
        await Task.Delay(random.Next(1000, 3001));

        ConsoleWriteLine($"Done cleaning system", ConsoleColor.Magenta);

        return new MethodResponse(200); // OK
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }

      return new MethodResponse(500); // error
    }

    private static async Task StartListeningForDesiredPropertyChanges(DeviceClient deviceClient)
    {
      try
      {
        ConsoleWriteLine();
        ConsoleWriteLine($"Will setup listener for desired property updates...", ConsoleColor.Green);

        // attach handler for desired property change
        await deviceClient.SetDesiredPropertyUpdateCallbackAsync(
          callback: DesiredPropertyUpdateCallback,
          userContext: deviceClient);

        ConsoleWriteLine($"Now listening for desired property updates", ConsoleColor.Green);
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static async Task DesiredPropertyUpdateCallback(TwinCollection desiredProperties, object userContext)
    {
      try
      {
        var deviceClient = (DeviceClient)userContext;

        ConsoleWriteLine();
        ConsoleWriteLine($"Received desired property update:", ConsoleColor.Green);
        ConsoleWriteLine(desiredProperties.ToJson(Formatting.Indented), ConsoleColor.Green);

        ApplyDesiredProperties(desiredProperties);

        // report back to the back-end
        await SendReportedProperties(deviceClient);
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static async Task SendReportedProperties(DeviceClient deviceClient)
    {
      try
      {
        ConsoleWriteLine();
        ConsoleWriteLine($"Will send reported properties for device {deviceId}...", ConsoleColor.Green);

        // we will send current state to IoT Central through the reported properties

        var reportedProperties = new TwinCollection();
        reportedProperties["BuildingID"] = "B.12345";
        reportedProperties["RoomNumber"] = 12;
        reportedProperties["TargetTemperature"] = targetTemperature;

        await deviceClient.UpdateReportedPropertiesAsync(reportedProperties);

        ConsoleWriteLine($"Successfully sent reported properties for {deviceId}:", ConsoleColor.Green);
        ConsoleWriteLine(reportedProperties.ToJson(Formatting.Indented), ConsoleColor.Green);
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    private static async Task SendDeviceDataUntilCancelled(DeviceClient deviceClient, CancellationToken cancellationToken)
    {
      try
      {
        while (!cancellationToken.IsCancellationRequested)
        {
          // create message

          switch (currentClimateControlState)
          {
            case heatingClimateControlState:
              currentTemperature += random.NextDouble();

              if (currentTemperature >= targetTemperature)
              {
                currentTemperature = targetTemperature;
                currentClimateControlState = idleClimateControlState;
              }

              break;

            case coolingClimateControlState:
              currentTemperature -= random.NextDouble();

              if (currentTemperature <= targetTemperature)
              {
                currentTemperature = targetTemperature;
                currentClimateControlState = idleClimateControlState;
              }
              break;
          }

          var humidityDeviation = random.NextDouble();
          if (random.Next(1, 3) == 2)
          {
            currentHumidity += humidityDeviation;
          }
          else
          {
            currentHumidity -= humidityDeviation;
          }

          if (currentHumidity < minimumHumidity)
          {
            currentHumidity = minimumHumidity;
          }
          else if (currentHumidity > maximumHumidity)
          {
            currentHumidity = maximumHumidity;
          }

          string warningEvent = null;
          if (random.Next(1, 6) == 3)
          {
            warningEvent = $"Warning: system error code {random.Next(1, 1001)}";
          }

          var payload = new
          {
            CurrentTemperature = currentTemperature,
            CurrentHumidity = currentHumidity,
            ClimateControlState = currentClimateControlState,
            WarningEvent = warningEvent
          };

          var bodyJson = JsonConvert.SerializeObject(payload, Formatting.Indented);
          var message = new Message(Encoding.UTF8.GetBytes(bodyJson))
          {
            ContentType = "application/json",
            ContentEncoding = "utf-8"
          };

          ConsoleColor? consoleColor = null;

          switch (currentClimateControlState)
          {
            case heatingClimateControlState:
              consoleColor = ConsoleColor.Yellow;
              break;

            case coolingClimateControlState:
              consoleColor = ConsoleColor.Cyan;
              break;
          }

          // send message

          if (consoleColor.HasValue)
          {
            ConsoleWriteLine();
            ConsoleWriteLine($"Current temperature: {currentTemperature}, target temperature: {targetTemperature}", consoleColor);
            ConsoleWriteLine($"Will send message for device {deviceId}:", consoleColor);
            ConsoleWriteLine(bodyJson, consoleColor);
          }

          await deviceClient.SendEventAsync(message);

          if (consoleColor.HasValue)
          {
            ConsoleWriteLine($"Successfully sent message for device {deviceId}", consoleColor);
          }
          // TODO: turn this on if you want to be reminded at all times that data is being sent
          //else
          //{
          //  Console.Write(".");
          //}

          await Task.Delay(1000);
        }
      }
      catch (Exception ex)
      {
        ConsoleWriteLine($"* ERROR * {ex.Message}", ConsoleColor.Red);
      }
    }

    #endregion

    #region Utility

    private static void ConsoleWriteLine(string message = null, ConsoleColor? foregroundColor = null)
    {
      lock (lockObject)
      {
        Console.ForegroundColor = foregroundColor ?? defaultConsoleForegroundColor;
        Console.WriteLine(message);
        Console.ForegroundColor = defaultConsoleForegroundColor;
      }
    }

    #endregion
  }
}
