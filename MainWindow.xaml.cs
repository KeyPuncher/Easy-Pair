using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace EasyPair
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        static byte[] FindBluetoothMAC()
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                //if (network.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx &&
                //    network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                //{
                //    network.GetPhysicalAddress().GetAddressBytes();
                //}

                if (network.Name.ToLower().Contains("bluetooth"))
                {
                    return network.GetPhysicalAddress().GetAddressBytes();
                }
            }

            return null;
        }

        public ObservableCollection<Info> Devices { get; private set; }

        DeviceWatcher _watcher;
        private IAsyncOperation<DevicePairingResult> _pairingTask;
        private TaskCompletionSource<string> _pinTask;
        private TaskCompletionSource<bool> _matchTask;

        public MainWindow()
        {
            InitializeComponent();
            Devices = new ObservableCollection<Info>();
            list.ItemsSource = Devices;
            list.SelectionChanged += OnSelectionChanged;

            string addr = BitConverter.ToString(FindBluetoothMAC());
            if (addr == null) addr = "";
            macAddress.Text = addr.Replace('-', ':');
        }

        void StartWatcher()
        {
            Devices.Clear();

            string selector = "(" + BluetoothDevice.GetDeviceSelectorFromPairingState(false) + ") AND (System.Devices.Aep.CanPair:=System.StructuredQueryType.Boolean#True)";

            _watcher = DeviceInformation.CreateWatcher(selector, null);
            _watcher.Added += OnDeviceAdded;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.EnumerationCompleted += OnCompleted;
            _watcher.Stopped += OnStopped;

            _watcher.Start();

            UpdateUI();
        }

        void StopWatcher()
        {
            if (_watcher != null)
            {
                _watcher.Added -= OnDeviceAdded;
                _watcher.Updated -= OnDeviceUpdated;
                _watcher.Removed -= OnDeviceRemoved;
                _watcher.EnumerationCompleted -= OnCompleted;

                if (_watcher.Status == DeviceWatcherStatus.Started || _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcher.Stop();
                }
            }

            UpdateUI();
        }

        void UpdateUI(string message = null)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                if (_watcher != null && _watcher.Status == DeviceWatcherStatus.Started || _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    btnStart.IsEnabled = false;
                    btnStop.IsEnabled = true;
                }
                else
                {
                    btnStart.IsEnabled = true;
                    btnStop.IsEnabled = false;
                }

                Info info = list.SelectedItem as Info;

                if (info != null && info.CanPair)
                {
                    btnPair.IsEnabled = true;
                    selectedLabel.Content = info.Name;
                }
                else
                {
                    status.Content = "";
                    btnPair.IsEnabled = false;
                    selectedLabel.Content = "None";
                }

                if (_pairingTask != null && _pairingTask.Status == AsyncStatus.Started)
                {
                    btnPair.IsEnabled = false;
                    list.IsEnabled = false;
                    btnAbort.IsEnabled = true;
                }
                else
                {
                    list.IsEnabled = true;
                    btnAbort.IsEnabled = false;
                }
                
                if (_pinTask != null)
                {
                    pin.IsEnabled = true;
                    btnPin.IsEnabled = true;
                    macAddress.IsEnabled = true;
                    btnMac.IsEnabled = true;
                }
                else
                {
                    pin.IsEnabled = false;
                    btnPin.IsEnabled = false;
                    macAddress.IsEnabled = false;
                    btnMac.IsEnabled = false;
                }

                if (_matchTask != null)
                {
                    btnAccept.Visibility = Visibility.Visible;
                    btnReject.Visibility = Visibility.Visible;
                }
                else
                {
                    btnAccept.Visibility = Visibility.Collapsed;
                    btnReject.Visibility = Visibility.Collapsed;
                }

                if (message != null)
                {
                    status.Content = message;
                }
            }));
        }

        async void HandlePairingRequest(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    args.Accept();
                    break;

                case DevicePairingKinds.ProvidePin:
                    var pinDefer = args.GetDeferral();

                    if (_pinTask != null)
                    {
                        _pinTask.SetResult(null);
                        _pinTask = null;
                    }
                        
                    _pinTask = new TaskCompletionSource<string>();
                    UpdateUI("Provide a PIN");

                    string pin = await _pinTask.Task;
                    if (!string.IsNullOrEmpty(pin))
                    {
                        await Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            args.Accept(pin);
                        }));
                    }
                    //await Task.Delay(5000);
                    pinDefer.Complete();
                    UpdateUI();
                    break;

                case DevicePairingKinds.DisplayPin:
                    args.Accept();
                    UpdateUI("Use this PIN: " + args.Pin);
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                    var displayDefer = args.GetDeferral();

                    if (_matchTask != null)
                    {
                        _matchTask.SetResult(false);
                        _matchTask = null;
                    }

                    _matchTask = new TaskCompletionSource<bool>();
                    UpdateUI("Confirm the PIN: " + args.Pin);

                    if (await _matchTask.Task)
                    {
                        args.Accept();
                    }

                    displayDefer.Complete();
                    UpdateUI();
                    break;
            }
        }

        #region Device Watcher Callbacks
        void OnDeviceAdded(DeviceWatcher watcher, DeviceInformation device)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                Devices.Add(new Info(device));
            }));
        }

        void OnDeviceUpdated(DeviceWatcher watcher, DeviceInformationUpdate update)
        {
            Info target = null;
            foreach (var info in Devices.ToArray())
            {
                if (info.Id == update.Id)
                {
                    target = info;
                    break;
                }
            }

            if (target == null) return;

            Dispatcher.Invoke(new Action(() =>
            {
                target.Update(update);

                CollectionViewSource.GetDefaultView(list.ItemsSource).Refresh();

                if (target == (list.SelectedItem as Info))
                {
                    UpdateUI();
                }
            }));
        }

        void OnDeviceRemoved(DeviceWatcher watcher, DeviceInformationUpdate update)
        {
            Info target = null;
            foreach (var info in Devices)
            {
                if (info.Id == update.Id)
                {
                    target = info;
                    break;
                }
            }

            if (target == null) return;

            Dispatcher.Invoke(new Action(() =>
            {
                Devices.Remove(target);
            }));
        }

        void OnCompleted(DeviceWatcher watcher, Object obj)
        {
            UpdateUI();
        }

        void OnStopped(DeviceWatcher watcher, Object obj)
        {
            UpdateUI();
        }
        #endregion

        #region UI Events

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartWatcher();
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopWatcher();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateUI();
        }

        private async void btnPair_Click(object sender, RoutedEventArgs e)
        {
            Info target = list.SelectedItem as Info;

            if (target != null)
            {
                DevicePairingKinds pairOptions = DevicePairingKinds.None;
                if (checkConfirm.IsChecked ?? false) pairOptions |= DevicePairingKinds.ConfirmOnly;
                if (checkProvide.IsChecked ?? false) pairOptions |= DevicePairingKinds.ProvidePin;
                if (checkDisplay.IsChecked ?? false) pairOptions |= DevicePairingKinds.DisplayPin;
                if (checkDisplay.IsChecked ?? false) pairOptions |= DevicePairingKinds.ConfirmPinMatch;

                DeviceInformationCustomPairing custom = target.Device.Pairing.Custom;
                custom.PairingRequested += HandlePairingRequest;
                _pairingTask = custom.PairAsync(pairOptions, DevicePairingProtectionLevel.None);
                UpdateUI();

                while (_pairingTask.Status == AsyncStatus.Started)
                {
                    await Task.Delay(500);
                }

                custom.PairingRequested -= HandlePairingRequest;
                UpdateUI();

                if (_pairingTask.Status == AsyncStatus.Completed)
                {
                    var result = _pairingTask.GetResults();
                    status.Content = result.Status.ToString();
                }

                _pairingTask = null;
            }
        }

        private void btnPin_Click(object sender, RoutedEventArgs e)
        {
            if (_pinTask != null)
            {
                _pinTask.SetResult(pin.Text);
                _pinTask = null;
            }
        }

        private void btnMac_Click(object sender, RoutedEventArgs e)
        {
            // Remove all non-hex characters
            StringBuilder sBuilder = new StringBuilder();
            foreach (char c in macAddress.Text.ToLower())
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                {
                    sBuilder.Append(c);
                }
            }
            
            // Convert from Hex string
            string hexString = sBuilder.ToString();
            sBuilder.Clear();
            for (int i = hexString.Length - 2; i >= 0; i -= 2)
            {
                if (i + 1 > hexString.Length) break;
                var b = Byte.Parse(hexString.Substring(i, 2), System.Globalization.NumberStyles.HexNumber);
                sBuilder.Append((char)b);
            }
            
            _pinTask.SetResult(sBuilder.ToString());
            _pinTask = null;
        }

        private void btnAccept_Click(object sender, RoutedEventArgs e)
        {
            if (_matchTask != null)
            {
                _matchTask.SetResult(true);
                _matchTask = null;
            }
        }

        private void btnReject_Click(object sender, RoutedEventArgs e)
        {
            if (_matchTask != null)
            {
                _matchTask.SetResult(false);
                _matchTask = null;
            }
        }

        private void btnAbort_Click(object sender, RoutedEventArgs e)
        {
            if (_pinTask != null)
            {
                _pinTask.SetResult(null);
                Task.Delay(1000).Wait();
                _pinTask = null;
            }

            UpdateUI();
        }
        #endregion

        public class Info
        {
            public string Name { get { return _info.Name; } }
            public string Id { get { return _info.Id; } }
            public bool CanPair { get { return _info.Pairing.CanPair; } }
            public DeviceInformation Device { get { return _info; } }

            private DeviceInformation _info;

            public Info(DeviceInformation deviceInfo)
            {
                _info = deviceInfo;
            }

            public void Update(DeviceInformationUpdate update)
            {
                _info.Update(update);
            }
        }
    }
}
