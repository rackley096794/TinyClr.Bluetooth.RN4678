using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Uart;
using GHIElectronics.TinyCLR.Pins;
using Ecu;
using System;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace PlaneComfort.TinyCLR.BlueTooth.RN4678
{
    /// <summary>
    /// This class implements the following bluetooth functions for the RN4678 module:
    ///
    ///   * Pin initialization
    ///   * RN4678 bluetooth module initialization
    ///   * Reader method to read incoming data from the BT module
    ///   * Writer methods to write commands to the BT module and write data to be passed through to the SPP device on the other end
    /// 
    /// </summary>
    internal class BluetoothClass
    {
        private static object writeLock = new object();

        private ECU Ecu;

        UartController BtUart;
        GpioPin BtP2_0;
        GpioPin BtP2_4;
        GpioPin BtEAN;
        GpioPin BtReset;

        private byte[] txBuffer = new byte[256];

        private bool SendPerfData = true;
        
        private StringBuilder serBuffer = new StringBuilder();
        private ArrayList BtIncoming = new ArrayList();
        private AutoResetEvent BtIncomingAvailable = new AutoResetEvent(false);
        private AutoResetEvent CmdReady = new AutoResetEvent(false);

        private string statusString;
        private string StatusString
        {
            get { return statusString; }
            set
            {
                statusString = value;
                switch (value)
                {
                    case "":
                        Status = BtStatus.Unknown;
                        break;
                    case null:
                        Status = BtStatus.Unknown;
                        break;
                    case "%CONNECT%":                // %CONNECT,404E365ABC50%
                        Status = BtStatus.NotReady;  
                        break;
                    case "%DISCONN%":
                        Status = BtStatus.NotReady;
                        break;
                    case "%RFCOMM_OPEN%":
                        Status = BtStatus.Ready;
                        break;
                    case "%RFCOMM_CLOSE%":
                        Status = BtStatus.NotReady;
                        break;
                    case "%REBOOT%":
                        Status = BtStatus.NotReady;
                        break;
                }
            }
        }
        private BtStatus Status;

        private enum BtStatus
        {
            Unknown,
            Ready,
            NotReady
        }

        private void ProcessIncomingCommand(string line)
        {
            switch(line)
            {
                case "help":
                    PrintHelp();
                    return;
                case "stop":
                    this.SendPerfData = false;
                    return;
                case "go":
                    this.SendPerfData = true;
                    return;
                case "save":  // Save settings to EEPROM
                    Ecu.Settings.WriteAllToConfigStorage();
                    SendData("Settings saved to EEPROM");
                    return;
                case "defaults":  // Save settings to EEPROM
                    Ecu.Settings.ResetToDefaults();
                    SendData("Reset to defaults.  Note: You still need to 'save' to save them to EEPROM.");
                    return;
            }
            
            if (!line.Contains(":"))
            {
                SendData("Error: Invalid command received.");
                return;
            }

            var cmd = line.Substring(0, line.IndexOf(":"));
            var val = line.Substring(line.IndexOf(":") + 1);
            if (cmd == null || cmd.Length == 0 || val == null || val.Length == 0)
                return;

            switch (cmd)
            {
                case "perf":  // Start or stop sending perf data
                    if (val == "1")
                        SendPerfData = true;
                    else
                        SendPerfData = false;
                    break;
                case "svp":  // Set voltage P    
                    Ecu.Settings.VoltsPidP = ToSingle(val);
                    Ecu.VoltsPid.PGain = ToSingle(val);
                    SendData($"VoltsPidP set to {val}");
                    break;
                case "svi":  // Set voltage I
                    Ecu.Settings.VoltsPidI = ToSingle(val);
                    Ecu.VoltsPid.IGain = ToSingle(val);
                    SendData($"VoltsPidI set to {val}");
                    break;
                case "svd":  // Set voltage D
                    Ecu.Settings.VoltsPidD = ToSingle(val);
                    Ecu.VoltsPid.DGain = ToSingle(val);
                    SendData($"VoltsPidD set to {val}");
                    break;
                case "sap":  // Set amps P
                    Ecu.Settings.AmpsPidP = ToSingle(val);
                    Ecu.AmpsPid.PGain = ToSingle(val);
                    SendData($"AmpsPidP set to {val}");
                    break;
                case "sai":  // Set amps I
                    Ecu.Settings.AmpsPidI = ToSingle(val);
                    Ecu.AmpsPid.IGain = ToSingle(val);
                    SendData($"AmpsPidI set to {val}");
                    break;
                case "sad":  // Set amps D
                    Ecu.Settings.AmpsPidD = ToSingle(val);
                    Ecu.AmpsPid.DGain = ToSingle(val);
                    SendData($"AmpsPidD set to {val}");
                    break;
            }        
        }
        
        private void PrintHelp()
        {
            SendData("Available commands:\r\n  help - print help\r\n  save - save settings to EEPROM\r\n  defaults - reset to defaults");
            SendData("Change settings commands formatted in two parts, command and value.  E.g.:\r\n  svi:0.5");
            SendData("Available settings:\r\n  svp:float \r\n  svi:float\r\n  svD:float\r\n  sap:float");
            SendData("  sai:float\r\n  sad:float\r\n  perf:1 or 0 - turns on/off perf output\r\n");
        }

        private float ToSingle(String value)
        {
            if (value == null)
                return 0;
            return (float)Convert.ToDouble(value);            
        }

        public BluetoothClass(ECU Ecu)
        {
            this.Ecu = Ecu;

            var Gpio = GpioController.GetDefault();
            BtP2_0 = Gpio.OpenPin(SC20100.GpioPin.PC6);
            BtP2_0.SetDriveMode(GpioPinDriveMode.Output);
            BtP2_0.Write(GpioPinValue.High);

            BtP2_4 = Gpio.OpenPin(SC20100.GpioPin.PD15);
            BtP2_4.SetDriveMode(GpioPinDriveMode.Output);
            BtP2_4.Write(GpioPinValue.High);

            BtEAN = Gpio.OpenPin(SC20100.GpioPin.PD14);
            BtEAN.SetDriveMode(GpioPinDriveMode.Output);
            BtEAN.Write(GpioPinValue.Low);

            // Do not reset!
            BtReset = Gpio.OpenPin(SC20100.GpioPin.PD10);
            BtReset.SetDriveMode(GpioPinDriveMode.Output);
            BtReset.Write(GpioPinValue.High);

            BtUart = UartController.FromName(SC20100.UartPort.Uart5);
            var uartSettings = new UartSetting()
            {
                BaudRate = 115200,
                DataBits = 8,
                Parity = UartParity.None,
                StopBits = UartStopBitCount.One,
                Handshaking = UartHandshake.RequestToSend
            };
            BtUart.SetActiveSettings(uartSettings);
            BtUart.Enable();


            //if (Ecu.Settings.BtInitialized == 0)
            //    Ser.BaudRate = 115200;
            //else
            //    Ser.BaudRate = 460800;           

            Thread ReadThread = new Thread(() => ReaderRoutine());
            ReadThread.Start();

            if (Ecu.Settings.BtInitialized == 0)
            {
                Thread CfgThread = new Thread(() => SendConfigCommands());
                CfgThread.Start();
            }
        }

        private void ReaderRoutine()
        {
            var rxBuffer = new byte[256];

            while (true)
            {
                Thread.Sleep(20);
                if (BtUart.BytesToRead > 0)
                {
                    var bytesReceived = BtUart.Read(rxBuffer, 0, BtUart.BytesToRead);
                    var justReceived = Encoding.UTF8.GetString(rxBuffer, 0, bytesReceived);
                    //Debug.WriteLine(justReceived);

                    for(int i=0; i<bytesReceived; i++)                 
                    {
                        char data = Convert.ToChar(rxBuffer[i]);
                        serBuffer.Append(data);

                        // Process it as needed
                        var str = serBuffer.ToString();

                        if (serBuffer.Length >= 2 &&
                            data == '%' && serBuffer[0] == '%')
                        {
                            Debug.WriteLine($"BT status: {str}");
                            this.StatusString = str;
                            serBuffer.Clear();
                        }

                        // Incoming from BT device
                        if (serBuffer.Length >= 2)
                        {
                            if (str.Contains("CMD> "))
                            {
                                Debug.WriteLine($"BT Data: {str}");
                                serBuffer.Clear();
                                CmdReady.Set();
                                continue;
                            }
                            if (str.Contains("AOK\r\n"))
                            {
                                str = serBuffer.Replace("\r\n", "").ToString();
                                Debug.WriteLine($"BT Data: {str}");
                                serBuffer.Clear();
                                continue;
                            }
                            if (str.Contains("\r\n"))
                            {
                                Debug.WriteLine($"BT Data: {str}");
                                str = serBuffer.Replace("\r\n", "").ToString();
                                serBuffer.Clear();
                                ProcessIncomingCommand(str);
                            }
                        }
                    }
                }
            }
        }

        private bool SendConfigCommands()
        {
            // Prevent writes by other threads
            Status = BtStatus.NotReady;
            Thread.Sleep(500);

            lock (writeLock)
            {
                SendCmd("$$$");                       // Enters command/config mode
                SendCmd("SG,0");                      // Dual mode // Bluetooth Classic mode only
                SendCmd("SA,2");                      // "Just Works" - no PIN prompting for pairing
                SendCmd("SN,PlaneComfort AC 2.0");    // Device name
                SendCmd("SQ,1000");                   // Hardware flow control, reboot after disconnect
                SendCmd("SY,4");                      // Max transmit power
                SendCmd("SL,05");                     // Scan for 50 seconds
                //SendCmd("SU,01");                   // 460800 bps
                SendCmd("R,1");                       // Reboot for changes to take effect
            }

            Ecu.Settings.BtInitialized = 1;
            Ecu.Settings.WriteBtInitializedToConfigStorage();

            //Ser.BaudRate = 460800;
            return true;
        }

        /// <summary>
        /// Only call after the module has been initialized with "$$$" so it knows to interpret this as a command
        /// </summary>
        /// <param name="cmd">Command without carrage return</param>
        /// <returns></returns>
        private bool SendCmd(string cmd)
        {
            Debug.WriteLine($"BT sending: {cmd}");
            WriteString(cmd + '\r');           

            var success = CmdReady.WaitOne(10000, false);
            return success;
        }

        /// <summary>
        /// Sends data over SPP
        /// </summary>
        /// <param name="DataString">Max 255 characters</param>
        /// <param name="perf">Indicates if data is streaming performance info so it can be discarded if perf output is disabled</param>
        /// <returns></returns>
        internal bool SendData(string DataString, bool perf = false)
        {
            // Check to see if we have a session open first
            if (Status != BtStatus.Ready)
                return false;

            if (perf == true && SendPerfData == false)
                return false;

            lock (writeLock)
            {                
                // If so, send it!
                WriteString(DataString + "\r\n");
                return true;
            }
        }    
        
        private void WriteString(string data)
        {
            if (data.Length < 255)
            {
                byte[] txBuffer = new byte[data.Length];
                txBuffer = Encoding.UTF8.GetBytes(data);
                var wbsize = BtUart.WriteBufferSize;
                var ret = BtUart.Write(txBuffer);

                //Ser.Flush();
                //var btw = Ser.BytesToWrite;

            }
            else
            {
                Debug.WriteLine("BT WriteString >= 255 chars! Too long!");
            }

        }
    }
}
