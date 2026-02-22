using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Global.Helper;
using NetLock_RMM_Relay_App.Global.Config;
using NetLock_RMM_Relay_App.RelayClient;

namespace NetLock_RMM_Relay_App.Windows
{
    public partial class SessionsWindow : Window
    {
        private readonly string _backendUrl;
        private readonly string _relayUrl;
        private readonly string _apiKey;
        private readonly string _hardwareId;
        private readonly RelayApiClient _apiClient;
        private readonly RelayPortManager _portManager;
        
        private string? _password; // For encryption/decryption
        private bool _isLocked = false;
        private bool _useRelayTls = false; // Enable/disable TLS for relay connections
        
        private ListBox? _sessionsListBox;
        private TextBox? _searchTextBox;
        private Button? _refreshButton;
        private Button? _connectAllButton;
        private Button? _disconnectAllButton;
        private Button? _lockButton;
        private Button? _themeAutoButton;
        private Button? _themeLightButton;
        private Button? _themeDarkButton;
        private TextBlock? _statusTextBlock;
        private Border? _errorTextBlock;
        private TextBlock? _errorMessageText;
        private StackPanel? _emptyTextBlock;
        private TextBlock? _backendUrlText;
        private TextBlock? _relayUrlText;
        private CheckBox? _relayTlsCheckBox;
        
        private ObservableCollection<SessionViewModel> _sessions = new ObservableCollection<SessionViewModel>();
        private ObservableCollection<SessionViewModel> _filteredSessions = new ObservableCollection<SessionViewModel>();
        
        // Grouped collections
        private ObservableCollection<SessionViewModel> _activeSessions = new ObservableCollection<SessionViewModel>();
        private ObservableCollection<SessionViewModel> _waitingSessions = new ObservableCollection<SessionViewModel>();
        private ObservableCollection<SessionViewModel> _disconnectingSessions = new ObservableCollection<SessionViewModel>();
        private ObservableCollection<SessionViewModel> _disabledSessions = new ObservableCollection<SessionViewModel>();
        
        private Dictionary<string, RelayConnection> _activeConnections = new Dictionary<string, RelayConnection>();
        private DispatcherTimer? _refreshTimer;
        private string _searchQuery = "";

        public SessionsWindow(string backendUrl, string relayUrl, string apiKey, string hardwareId, string? password = null, bool useRelayTls = false)
        {
            _backendUrl = backendUrl;
            _relayUrl = relayUrl;
            _apiKey = apiKey;
            _hardwareId = hardwareId;
            _password = password;
            _useRelayTls = useRelayTls;
            _apiClient = new RelayApiClient(backendUrl, apiKey, hardwareId);
            _portManager = new RelayPortManager(apiKey);
            
            InitializeComponent();
            InitializeControls();
            StartAutoRefresh();
            
            _ = LoadSessionsAsync();
            _ = RestoreSessionStatesAsync();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void InitializeControls()
        {
            _sessionsListBox = this.FindControl<ListBox>("SessionsListBox");
            _searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            _refreshButton = this.FindControl<Button>("RefreshButton");
            _connectAllButton = this.FindControl<Button>("ConnectAllButton");
            _disconnectAllButton = this.FindControl<Button>("DisconnectAllButton");
            _lockButton = this.FindControl<Button>("LockButton");
            _themeAutoButton = this.FindControl<Button>("ThemeAutoButton");
            _themeLightButton = this.FindControl<Button>("ThemeLightButton");
            _themeDarkButton = this.FindControl<Button>("ThemeDarkButton");
            _statusTextBlock = this.FindControl<TextBlock>("StatusTextBlock");
            _errorTextBlock = this.FindControl<Border>("ErrorTextBlock");
            _errorMessageText = this.FindControl<TextBlock>("ErrorMessageText");
            _emptyTextBlock = this.FindControl<StackPanel>("EmptyTextBlock");
            _backendUrlText = this.FindControl<TextBlock>("BackendUrlText");
            _relayUrlText = this.FindControl<TextBlock>("RelayUrlText");
            _relayTlsCheckBox = this.FindControl<CheckBox>("RelayTlsCheckBox");
            
            if (_sessionsListBox != null)
                _sessionsListBox.ItemsSource = _filteredSessions;
            
            // Set URL texts
            if (_backendUrlText != null)
                _backendUrlText.Text = _backendUrl;
            
            if (_relayUrlText != null)
                _relayUrlText.Text = _relayUrl;
            
            // Set TLS checkbox state
            if (_relayTlsCheckBox != null)
            {
                _relayTlsCheckBox.IsChecked = _useRelayTls;
                _relayTlsCheckBox.IsCheckedChanged += RelayTlsCheckBox_CheckedChanged;
            }
            
            // Wire up search event
            if (_searchTextBox != null)
            {
                _searchTextBox.TextChanged += SearchTextBox_TextChanged;
            }
            
            // Show/hide lock button based on password availability
            if (_lockButton != null)
            {
                _lockButton.IsVisible = !string.IsNullOrEmpty(_password);
            }
            
            // Update theme button states
            UpdateThemeButtonStates();
        }

        private void StartAutoRefresh()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _refreshTimer.Tick += async (_, _) => await LoadSessionsAsync();
            _refreshTimer.Start();
        }

        private async Task LoadSessionsAsync()
        {
            try
            {
                if (_errorTextBlock != null)
                    _errorTextBlock.IsVisible = false;
                    
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = "Loading sessions...";
                
                var (success, sessions, error) = await _apiClient.GetAvailableSessions();
                
                if (!success)
                {
                    if (_errorTextBlock != null && _errorMessageText != null)
                    {
                        _errorMessageText.Text = error ?? "Unknown error";
                        _errorTextBlock.IsVisible = true;
                    }
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "Error";
                    return;
                }
                
                // Update sessions list on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Remove sessions that no longer exist
                    var sessionIds = sessions.Select(s => s.session_id).ToHashSet();
                    var toRemove = _sessions.Where(s => !sessionIds.Contains(s.SessionInfo.session_id)).ToList();
                    
                    foreach (var session in toRemove)
                    {
                        _sessions.Remove(session);
                        
                        // Disconnect if connected
                        if (_activeConnections.ContainsKey(session.SessionInfo.session_id))
                        {
                            _activeConnections[session.SessionInfo.session_id].DisconnectAsync();
                            _activeConnections.Remove(session.SessionInfo.session_id);
                        }
                    }

                    // Add new sessions or update existing
                    foreach (var sessionInfo in sessions)
                    {
                        var existing = _sessions.FirstOrDefault(s => 
                            s.SessionInfo.session_id == sessionInfo.session_id);
                        
                        if (existing != null)
                        {
                            // Update existing session info
                            existing.SessionInfo = sessionInfo;
                        }
                        else
                        {
                            // Add new session
                            var newViewModel = new SessionViewModel(sessionInfo);
                            
                            // Subscribe to auto-connect changes
                            newViewModel.AutoConnectChanged += async (s, autoConnect) =>
                            {
                                await SaveSessionStatesAsync();
                            };
                            
                            _sessions.Add(newViewModel);
                        }
                    }

                    // Update UI state
                    if (_emptyTextBlock != null)
                        _emptyTextBlock.IsVisible = _sessions.Count == 0;
                    
                    // Apply search filter and grouping
                    ApplySearchFilter();
                    
                    if (_connectAllButton != null)
                    {
                        // Enable Connect All only if there are disconnected sessions that are enabled AND the device is ready
                        var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true &&
                            (s.SessionInfo?.is_device_ready == true || s.SessionInfo?.display_status == "Ready to connect"));
                        _connectAllButton.IsEnabled = disconnectedCount > 0;
                    }
                    
                    if (_disconnectAllButton != null)
                        _disconnectAllButton.IsEnabled = _activeConnections.Count > 0;
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = $"Last updated: {DateTime.Now:HH:mm:ss} - {_sessions.Count} session(s)";
                });
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "LoadSessionsAsync", ex.ToString());
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = ex.Message;
                    _errorTextBlock.IsVisible = true;
                }
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = "Error";
            }
        }
        
        private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _searchQuery = textBox.Text ?? "";
                ApplySearchFilter();
            }
        }
        
        private void ApplySearchFilter()
        {
            _filteredSessions.Clear();
            _activeSessions.Clear();
            _waitingSessions.Clear();
            _disconnectingSessions.Clear();
            _disabledSessions.Clear();
            
            var query = _searchQuery.ToLower();
            
            foreach (var session in _sessions)
            {
                // Apply search filter
                bool matchesSearch = string.IsNullOrEmpty(query) ||
                    session.SessionInfo.session_id.ToLower().Contains(query) ||
                    session.TargetDeviceDisplay.ToLower().Contains(query) ||
                    session.OrganizationDisplay.ToLower().Contains(query) ||
                    session.SessionInfo.target_port.ToString().Contains(query);
                
                if (!matchesSearch)
                    continue;
                
                // Add to filtered list
                _filteredSessions.Add(session);
                
                // Group by status
                if (session.IsConnectedLocal || (session.SessionInfo?.is_active == true && session.SessionInfo?.enabled == true))
                {
                    _activeSessions.Add(session);
                }
                else if (session.SessionInfo?.is_active == false && session.SessionInfo?.enabled == true)
                {
                    _waitingSessions.Add(session);
                }
                else if (session.SessionInfo?.enabled == false && session.SessionInfo?.is_active == true)
                {
                    _disconnectingSessions.Add(session);
                }
                else if (session.SessionInfo?.enabled == false)
                {
                    _disabledSessions.Add(session);
                }
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadSessionsAsync();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not SessionViewModel viewModel)
                return;

            try
            {
                var sessionInfo = viewModel.SessionInfo;
                
                // Check if another admin is already connected to THIS DEVICE (Single-Admin-Policy per device)
                // We need to check if ANY session to the same target_device_id is already active
                var targetDeviceId = sessionInfo.target_device_id;
                bool anySessionActiveForDevice = _sessions
                    .Where(s => s.SessionInfo?.target_device_id == targetDeviceId)
                    .Any(s => s.SessionInfo?.is_active == true);

                // Ensure device is ready before allowing connect
                bool deviceReady = sessionInfo.is_device_ready == true || sessionInfo.display_status == "Ready to connect";
                if (!deviceReady)
                {
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "Device is offline or not ready to connect.";

                    Logging.Info("SessionsWindow", "ConnectButton_Click", $"Prevented connect to {sessionInfo.session_id} because device not ready");

                    // Reset state on UI thread in case it was toggled
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnecting = false;
                        viewModel.IsConnectedLocal = false;
                        viewModel.LocalPort = 0;
                    });

                    return;
                }

                if (anySessionActiveForDevice)
                {
                    var result = await ShowActiveConnectionWarning(0); // Parameter not used
                    if (!result)
                    {
                        // User cancelled connection
                        Logging.Info("SessionsWindow", "ConnectButton_Click", 
                            $"User cancelled connection to device {targetDeviceId} - another admin is already connected to this device");
                        return;
                    }
                }
                
                // Set connecting state in UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    viewModel.IsConnecting = true;
                });
                
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = $"Connecting to session {sessionInfo.session_id}...";

                // Get or assign persistent port for this session
                int localPort = _portManager.GetOrAssignPort(sessionInfo.session_id);

                var connection = new RelayConnection(_backendUrl, _relayUrl, _apiKey, _hardwareId, _useRelayTls);
                
                // Subscribe to connection events for auto-reconnect
                connection.ConnectionLost += async (sender, error) =>
                {
                    await HandleConnectionLost(viewModel, connection, error);
                };
                
                connection.Reconnected += (sender, args) =>
                {
                    HandleReconnected(viewModel);
                };
                
                // Subscribe to active connections changed event
                connection.ActiveConnectionsChanged += (sender, count) =>
                {
                    HandleActiveConnectionsChanged(viewModel, count);
                };
                
                // Subscribe to throughput changed event
                connection.ThroughputChanged += (sender, throughput) =>
                {
                    HandleThroughputChanged(viewModel, connection, throughput.upload, throughput.download);
                };
                
                // Subscribe to ping changed event
                connection.PingChanged += (sender, ping) =>
                {
                    HandlePingChanged(viewModel, ping.current, ping.average);
                };
                
                // Subscribe to kicked event (Single-Admin-Policy enforcement)
                connection.OnKicked += async (sender, kickArgs) =>
                {
                    await HandleSessionKicked(viewModel, kickArgs);
                };
                
                var (success, error) = await connection.ConnectToSession(sessionInfo.session_id, localPort);

                if (!success)
                {
                    if (_errorTextBlock != null && _errorMessageText != null)
                    {
                        _errorMessageText.Text = "Failed to connect: " + error;
                        _errorTextBlock.IsVisible = true;
                    }
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "Connection failed";
                    
                    // Reset state on UI thread
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnecting = false;
                        viewModel.IsConnectedLocal = false;
                        viewModel.LocalPort = 0;
                    });
                    return;
                }
                
                _activeConnections[sessionInfo.session_id] = connection;
                
                // Update ViewModel on UI thread to ensure binding updates properly
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    viewModel.IsConnecting = false;
                    viewModel.IsConnectedLocal = true;
                    viewModel.LocalPort = localPort;
                });
                
                if (_statusTextBlock != null)
                {
                    string deviceDisplay = !string.IsNullOrEmpty(sessionInfo.target_device_name) 
                        ? sessionInfo.target_device_name 
                        : sessionInfo.target_device_id;
                    _statusTextBlock.Text = $"Connected! Local port {localPort} is now forwarding to {deviceDisplay}:{sessionInfo.target_port}";
                }
                
                if (_connectAllButton != null)
                {
                    var disconnectedCount = _sessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting);
                    _connectAllButton.IsEnabled = disconnectedCount > 0;
                }
                
                if (_disconnectAllButton != null)
                    _disconnectAllButton.IsEnabled = true;
                
                Logging.Debug("SessionsWindow", "ConnectButton_Click", 
                    $"Connected to session {sessionInfo.session_id} on local port {localPort}");
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "ConnectButton_Click", ex.ToString());
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = ex.Message;
                    _errorTextBlock.IsVisible = true;
                }
                
                // Reset state on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    viewModel.IsConnecting = false;
                    viewModel.IsConnectedLocal = false;
                    viewModel.LocalPort = 0;
                });
            }
        }

        private async void CopyConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not SessionViewModel viewModel)
                return;

            try
            {
                if (!viewModel.IsConnectedLocal || viewModel.LocalPort == 0)
                {
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "Not connected - cannot copy connection string";
                    return;
                }

                // Create connection string
                string connectionString = $"localhost:{viewModel.LocalPort}";
                
                // Copy to clipboard
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(connectionString);
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = $"Copied '{connectionString}' to clipboard";
                    
                    Logging.Debug("SessionsWindow", "CopyConnectionButton_Click", 
                        $"Copied connection string: {connectionString}");
                }
                else
                {
                    if (_errorTextBlock != null && _errorMessageText != null)
                    {
                        _errorMessageText.Text = "Failed to access clipboard";
                        _errorTextBlock.IsVisible = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "CopyConnectionButton_Click", ex.ToString());
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = $"Failed to copy: {ex.Message}";
                    _errorTextBlock.IsVisible = true;
                }
            }
        }

        private async void ConnectAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_connectAllButton != null)
                    _connectAllButton.IsEnabled = false;
                
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = "Connecting to all sessions...";
                
                int successCount = 0;
                int failCount = 0;
                
                // Get all disconnected sessions that are enabled and whose device is ready
                var disconnectedSessions = _filteredSessions
                    .Where(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true &&
                        (s.SessionInfo?.is_device_ready == true || s.SessionInfo?.display_status == "Ready to connect"))
                     .ToList();
                
                foreach (var viewModel in disconnectedSessions)
                {
                    try
                    {
                        var sessionInfo = viewModel.SessionInfo;
                        
                        // Set connecting state
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            viewModel.IsConnecting = true;
                        });
                        
                        // Get or assign persistent port for this session
                        int localPort = _portManager.GetOrAssignPort(sessionInfo.session_id);

                        var connection = new RelayConnection(_backendUrl, _relayUrl, _apiKey, _hardwareId, _useRelayTls);
                        
                        // Subscribe to connection events for auto-reconnect
                        connection.ConnectionLost += async (sender, error) =>
                        {
                            await HandleConnectionLost(viewModel, connection, error);
                        };
                        
                        connection.Reconnected += (sender, args) =>
                        {
                            HandleReconnected(viewModel);
                        };
                        
                        // Subscribe to active connections changed event
                        connection.ActiveConnectionsChanged += (sender, count) =>
                        {
                            HandleActiveConnectionsChanged(viewModel, count);
                        };
                        
                        // Subscribe to throughput changed event
                        connection.ThroughputChanged += (sender, throughput) =>
                        {
                            HandleThroughputChanged(viewModel, connection, throughput.upload, throughput.download);
                        };
                        
                        // Subscribe to ping changed event
                        connection.PingChanged += (sender, ping) =>
                        {
                            HandlePingChanged(viewModel, ping.current, ping.average);
                        };
                        
                        // Subscribe to kicked event (Single-Admin-Policy enforcement)
                        connection.OnKicked += async (sender, kickArgs) =>
                        {
                            await HandleSessionKicked(viewModel, kickArgs);
                        };
                        
                        var (success, error) = await connection.ConnectToSession(sessionInfo.session_id, localPort);

                        if (success)
                        {
                            _activeConnections[sessionInfo.session_id] = connection;
                            
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                viewModel.IsConnecting = false;
                                viewModel.IsConnectedLocal = true;
                                viewModel.LocalPort = localPort;
                            });
                            
                            successCount++;
                            
                            Logging.Debug("SessionsWindow", "ConnectAllButton_Click", 
                                $"Connected to session {sessionInfo.session_id} on local port {localPort}");
                        }
                        else
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                viewModel.IsConnecting = false;
                                viewModel.IsConnectedLocal = false;
                                viewModel.LocalPort = 0;
                            });
                            
                            failCount++;
                            
                            Logging.Error("SessionsWindow", "ConnectAllButton_Click", 
                                $"Failed to connect to session {sessionInfo.session_id}: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        Logging.Error("SessionsWindow", "ConnectAllButton_Click", 
                            $"Error connecting to session: {ex.Message}");
                        
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            viewModel.IsConnecting = false;
                            viewModel.IsConnectedLocal = false;
                            viewModel.LocalPort = 0;
                        });
                    }
                }
                
                // Update status
                if (_statusTextBlock != null)
                {
                    if (failCount == 0)
                        _statusTextBlock.Text = $"Successfully connected to all {successCount} session(s)";
                    else
                        _statusTextBlock.Text = $"Connected to {successCount} session(s), {failCount} failed";
                }
                
                // Update button states
                if (_connectAllButton != null)
                {
                    var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true &&
                        (s.SessionInfo?.is_device_ready == true || s.SessionInfo?.display_status == "Ready to connect"));
                    _connectAllButton.IsEnabled = disconnectedCount > 0;
                }
                
                if (_disconnectAllButton != null)
                    _disconnectAllButton.IsEnabled = _activeConnections.Count > 0;
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "ConnectAllButton_Click", ex.ToString());
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = $"Error during connect all: {ex.Message}";
                    _errorTextBlock.IsVisible = true;
                }
                
                if (_connectAllButton != null)
                {
                    var disconnectedCount = _sessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting);
                    _connectAllButton.IsEnabled = disconnectedCount > 0;
                }
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not SessionViewModel viewModel)
                return;

            try
            {
                var sessionId = viewModel.SessionInfo.session_id;
                
                if (_activeConnections.ContainsKey(sessionId))
                {
                    // Call async disconnect to notify server
                    await _activeConnections[sessionId].DisconnectAsync();
                    _activeConnections.Remove(sessionId);
                    
                    // Update view model state on UI thread to ensure binding updates
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnectedLocal = false;
                        viewModel.LocalPort = 0;
                    });
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = $"Disconnected from session {sessionId}";
                    
                    if (_connectAllButton != null)
                    {
                        var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true);
                        _connectAllButton.IsEnabled = disconnectedCount > 0;
                    }
                    
                    if (_disconnectAllButton != null)
                        _disconnectAllButton.IsEnabled = _activeConnections.Count > 0;
                    
                    Logging.Debug("SessionsWindow", "DisconnectButton_Click", 
                        $"Disconnected from session {sessionId}");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "DisconnectButton_Click", ex.ToString());
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = ex.Message;
                    _errorTextBlock.IsVisible = true;
                }
            }
        }

        private async void DisconnectAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disconnect all connections asynchronously
                var disconnectTasks = _activeConnections.Values
                    .Select(connection => connection.DisconnectAsync())
                    .ToList();
                
                await Task.WhenAll(disconnectTasks);
                
                // Update all view models on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var session in _sessions)
                    {
                        if (session.IsConnectedLocal)
                        {
                            session.IsConnectedLocal = false;
                            session.LocalPort = 0;
                        }
                    }
                });
                
                _activeConnections.Clear();
                
                if (_connectAllButton != null)
                {
                    var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true);
                    _connectAllButton.IsEnabled = disconnectedCount > 0;
                }
                
                if (_disconnectAllButton != null)
                    _disconnectAllButton.IsEnabled = false;
                    
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = "Disconnected from all sessions";

                Logging.Debug("SessionsWindow", "DisconnectAllButton_Click", 
                    "Disconnected from all sessions");
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "DisconnectAllButton_Click", ex.ToString());
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Stop refresh timer
            if (_refreshTimer != null)
                _refreshTimer.Stop();
            
            // Disconnect all connections asynchronously
            var disconnectTasks = _activeConnections.Values
                .Select(async connection =>
                {
                    try
                    {
                        await connection.DisconnectAsync();
                    }
                    catch (Exception ex)
                    {
                        Logging.Error("SessionsWindow", "OnClosing", 
                            $"Error disconnecting: {ex.Message}");
                    }
                })
                .ToArray();
            
            // Wait for all disconnects to complete (with timeout) - synchronous wait
            try
            {
                Task.WhenAll(disconnectTasks).Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                Logging.Error("SessionsWindow", "OnClosing", 
                    "Disconnect timeout or error - some connections may not have been properly closed");
            }
            
            _activeConnections.Clear();
            
            base.OnClosing(e);
        }
        
        private void ThemeAutoButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Instance.CurrentMode = ThemeManager.ThemeMode.Auto;
            UpdateThemeButtonStates();
            Logging.Debug("SessionsWindow", "ThemeAutoButton_Click", "Theme set to Auto");
        }
        
        private void ThemeLightButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Instance.CurrentMode = ThemeManager.ThemeMode.Light;
            UpdateThemeButtonStates();
            Logging.Debug("SessionsWindow", "ThemeLightButton_Click", "Theme set to Light");
        }
        
        private void ThemeDarkButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Instance.CurrentMode = ThemeManager.ThemeMode.Dark;
            UpdateThemeButtonStates();
            Logging.Debug("SessionsWindow", "ThemeDarkButton_Click", "Theme set to Dark");
        }
        
        private void UpdateThemeButtonStates()
        {
            var currentMode = ThemeManager.Instance.CurrentMode;
            
            // Visual feedback: slightly change opacity or style for active button
            if (_themeAutoButton != null)
                _themeAutoButton.Opacity = currentMode == ThemeManager.ThemeMode.Auto ? 1.0 : 0.7;
            
            if (_themeLightButton != null)
                _themeLightButton.Opacity = currentMode == ThemeManager.ThemeMode.Light ? 1.0 : 0.7;
            
            if (_themeDarkButton != null)
                _themeDarkButton.Opacity = currentMode == ThemeManager.ThemeMode.Dark ? 1.0 : 0.7;
        }
        
        private async void RelayTlsCheckBox_CheckedChanged(object? sender, RoutedEventArgs e)
        {
            if (_relayTlsCheckBox == null)
                return;
                
            _useRelayTls = _relayTlsCheckBox.IsChecked ?? false;
            
            Logging.Info("SessionsWindow", "RelayTlsCheckBox_CheckedChanged", 
                $"Relay TLS changed to: {_useRelayTls}");
            
            // Warn user if there are active connections
            if (_activeConnections.Count > 0)
            {
                if (_statusTextBlock != null)
                {
                    _statusTextBlock.Text = $"⚠️ TLS setting changed. This will only affect NEW connections. " +
                        $"Reconnect existing sessions ({_activeConnections.Count} active) to apply the change.";
                }
                
                Logging.Info("SessionsWindow", "RelayTlsCheckBox_CheckedChanged", 
                    $"TLS setting changed with {_activeConnections.Count} active connection(s)");
            }
            else
            {
                if (_statusTextBlock != null)
                {
                    _statusTextBlock.Text = $"TLS setting changed to: {(_useRelayTls ? "Enabled" : "Disabled")}. " +
                        "This will be used for all new connections.";
                }
            }
            
            // Save configuration if password is available
            if (!string.IsNullOrEmpty(_password))
            {
                var config = new SecureConfig.ConfigData
                {
                    BackendUrl = _backendUrl,
                    RelayUrl = _relayUrl,
                    ApiKey = _apiKey,
                    HardwareId = _hardwareId,
                    UseRelayTls = _useRelayTls
                };
                
                if (SecureConfig.SaveConfig(config, _password))
                {
                    Logging.Info("SessionsWindow", "RelayTlsCheckBox_CheckedChanged", 
                        "Configuration saved successfully");
                }
                else
                {
                    Logging.Error("SessionsWindow", "RelayTlsCheckBox_CheckedChanged", 
                        "Failed to save configuration");
                }
            }
        }

        private async Task HandleConnectionLost(SessionViewModel viewModel, RelayConnection connection, string error)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Logging.Error("SessionsWindow", "HandleConnectionLost",
                    $"Connection lost for session {viewModel.SessionInfo.session_id}: {error}");
                
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = $"Connection lost for {viewModel.TargetDeviceDisplay}, attempting to reconnect...";
                
                // Set reconnecting state
                viewModel.IsConnecting = true;
                
                // Attempt reconnect
                var (success, reconnectError) = await connection.ReconnectAsync();
                
                if (success)
                {
                    Logging.Info("SessionsWindow", "HandleConnectionLost",
                        $"Successfully reconnected session {viewModel.SessionInfo.session_id}");
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = $"Reconnected to {viewModel.TargetDeviceDisplay}";
                }
                else
                {
                    Logging.Error("SessionsWindow", "HandleConnectionLost",
                        $"Failed to reconnect session {viewModel.SessionInfo.session_id}: {reconnectError}");
                    
                    // Update UI to show disconnected state
                    viewModel.IsConnecting = false;
                    viewModel.IsConnectedLocal = false;
                    viewModel.LocalPort = 0;
                    
                    // Remove from active connections
                    if (_activeConnections.ContainsKey(viewModel.SessionInfo.session_id))
                    {
                        _activeConnections.Remove(viewModel.SessionInfo.session_id);
                    }
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = $"Failed to reconnect to {viewModel.TargetDeviceDisplay}: {reconnectError}";
                    
                    if (_errorTextBlock != null && _errorMessageText != null)
                    {
                        _errorMessageText.Text = $"Connection to {viewModel.TargetDeviceDisplay} was lost and could not be reconnected: {reconnectError}";
                        _errorTextBlock.IsVisible = true;
                    }
                    
                    // Update button states
                    if (_connectAllButton != null)
                    {
                        var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true);
                        _connectAllButton.IsEnabled = disconnectedCount > 0;
                    }
                    
                    if (_disconnectAllButton != null)
                        _disconnectAllButton.IsEnabled = _activeConnections.Count > 0;
                }
            });
        }
        
        private void HandleReconnected(SessionViewModel viewModel)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.IsConnecting = false;
                viewModel.IsConnectedLocal = true;
                
                Logging.Info("SessionsWindow", "HandleReconnected",
                    $"Session {viewModel.SessionInfo.session_id} reconnected successfully");
            });
        }
        
        /// <summary>
        /// Handles session kicked event (Single-Admin-Policy enforcement)
        /// </summary>
        private async Task HandleSessionKicked(SessionViewModel viewModel, RelayClient.KickEventArgs kickArgs)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Logging.Info("SessionsWindow", "HandleSessionKicked",
                    $"Session {viewModel.SessionInfo.session_id} was kicked: {kickArgs.Reason}");
                
                // Update UI to show disconnected state
                viewModel.IsConnecting = false;
                viewModel.IsConnectedLocal = false;
                viewModel.LocalPort = 0;
                viewModel.ActiveConnectionCount = 0;
                viewModel.HasLocalAppConnected = false;
                viewModel.UploadSpeed = 0;
                viewModel.DownloadSpeed = 0;
                
                // Remove from active connections
                if (_activeConnections.ContainsKey(viewModel.SessionInfo.session_id))
                {
                    _activeConnections.Remove(viewModel.SessionInfo.session_id);
                }
                
                // Show user-friendly message
                string userMessage = kickArgs.Reason == "new_admin_connected"
                    ? "Another admin connected to this device. Clicking connect will disconnect the other admin."
                    : "You were disconnected by the server.";
                
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = userMessage;
                
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = $"Connection to {viewModel.TargetDeviceDisplay} was terminated: {userMessage}";
                    _errorTextBlock.IsVisible = true;
                }
                
                // Update button states
                if (_connectAllButton != null)
                {
                    var disconnectedCount = _filteredSessions.Count(s => !s.IsConnectedLocal && !s.IsConnecting && s.SessionInfo?.enabled == true);
                    _connectAllButton.IsEnabled = disconnectedCount > 0;
                }
                
                if (_disconnectAllButton != null)
                    _disconnectAllButton.IsEnabled = _activeConnections.Count > 0;
                
                Logging.Info("SessionsWindow", "HandleSessionKicked",
                    $"Session {viewModel.SessionInfo.session_id} cleaned up after kick");
            });
        }
        
        private void HandleActiveConnectionsChanged(SessionViewModel viewModel, int count)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.ActiveConnectionCount = count;
                viewModel.HasLocalAppConnected = count > 0;
                
                Logging.Debug("SessionsWindow", "HandleActiveConnectionsChanged",
                    $"Session {viewModel.SessionInfo.session_id} now has {count} active connection(s)");
                
                // Update status text for better feedback
                if (_statusTextBlock != null && count > 0)
                {
                    _statusTextBlock.Text = $"{count} application(s) connected to {viewModel.TargetDeviceDisplay}";
                }
            });
        }
        
        private void HandleThroughputChanged(SessionViewModel viewModel, RelayConnection connection, long uploadSpeed, long downloadSpeed)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.UploadSpeed = uploadSpeed;
                viewModel.DownloadSpeed = downloadSpeed;
                viewModel.TotalUpload = connection.TotalBytesUpload;
                viewModel.TotalDownload = connection.TotalBytesDownload;
            });
        }
        
        private void HandlePingChanged(SessionViewModel viewModel, long currentPing, long averagePing)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.CurrentPing = currentPing;
                viewModel.AveragePing = averagePing;
            });
        }
        
        private async void LockButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_password))
                {
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "No password set - cannot lock";
                    return;
                }
                
                _isLocked = true;
                
                // Save current session states before locking
                await SaveSessionStatesAsync();
                
                if (_statusTextBlock != null)
                    _statusTextBlock.Text = "Application locked";
                
                Logging.Info("SessionsWindow", "LockButton_Click", "Application locked");
                
                // Hide the sessions window
                Hide();
                
                // Show unlock dialog as standalone window (parent is hidden)
                var unlockWindow = new UnlockWindow(SecureConfig.HashPassword(_password))
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                
                unlockWindow.Show();
                
                // Wait for the window to close
                await WaitForWindowClose(unlockWindow);
                
                if (unlockWindow.Unlocked)
                {
                    _isLocked = false;
                    
                    // Show the sessions window again
                    Show();
                    Activate();
                    
                    if (_statusTextBlock != null)
                        _statusTextBlock.Text = "Application unlocked";
                    
                    Logging.Info("SessionsWindow", "LockButton_Click", "Application unlocked");
                }
                else
                {
                    // User cancelled - close application
                    Logging.Info("SessionsWindow", "LockButton_Click", "Unlock cancelled - closing application");
                    Close();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "LockButton_Click", ex.ToString());
                
                // Show window again in case of error
                Show();
                
                if (_errorTextBlock != null && _errorMessageText != null)
                {
                    _errorMessageText.Text = $"Lock error: {ex.Message}";
                    _errorTextBlock.IsVisible = true;
                }
            }
        }
        
        private async Task SaveSessionStatesAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_password))
                    return;
                
                var states = new SecureConfig.SessionStateData();
                
                foreach (var session in _sessions)
                {
                    states.Sessions[session.SessionInfo.session_id] = new SecureConfig.SessionState
                    {
                        SessionId = session.SessionInfo.session_id,
                        AutoConnect = session.AutoConnect,
                        PreferredPort = session.LocalPort,
                        LastConnected = DateTime.UtcNow
                    };
                }
                
                SecureConfig.SaveSessionStates(states, _password);
                
                Logging.Debug("SessionsWindow", "SaveSessionStatesAsync", 
                    $"Saved {states.Sessions.Count} session states");
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "SaveSessionStatesAsync", ex.ToString());
            }
        }
        
        private async Task RestoreSessionStatesAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_password))
                    return;

                var (success, states) = SecureConfig.LoadSessionStates(_password);

                if (!success || states == null)
                    return;

                Logging.Debug("SessionsWindow", "RestoreSessionStatesAsync", 
                    $"Loaded {states.Sessions.Count} session states");

                // Wait for sessions to be loaded first
                await Task.Delay(1000);

                // Restore auto-connect flags
                foreach (var session in _sessions)
                {
                    if (states.Sessions.TryGetValue(session.SessionInfo.session_id, out var state))
                    {
                        session.AutoConnect = state.AutoConnect;

                        // Auto-connect if enabled and device is ready
                        bool deviceReady = session.SessionInfo.is_device_ready == true || session.SessionInfo.display_status == "Ready to connect";
                        if (state.AutoConnect && !session.IsConnectedLocal && deviceReady)
                        {
                            Logging.Info("SessionsWindow", "RestoreSessionStatesAsync", 
                                $"Auto-connecting to session {session.SessionInfo.session_id}");
                            
                            await ConnectToSessionAsync(session, state.PreferredPort);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "RestoreSessionStatesAsync", ex.ToString());
            }
        }

        private async Task ConnectToSessionAsync(SessionViewModel viewModel, int preferredPort = 0)
        {
            try
            {
                var sessionInfo = viewModel.SessionInfo;

                // Ensure device is ready before attempting connect
                bool deviceReady = sessionInfo.is_device_ready == true || sessionInfo.display_status == "Ready to connect";
                if (!deviceReady)
                {
                    Logging.Info("SessionsWindow", "ConnectToSessionAsync", $"Skipping connect for {sessionInfo.session_id}: device not ready");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnecting = false;
                        viewModel.IsConnectedLocal = false;
                        viewModel.LocalPort = 0;
                    });
                    return;
                }
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    viewModel.IsConnecting = true;
                });
                
                int localPort = preferredPort > 0 
                    ? preferredPort 
                    : _portManager.GetOrAssignPort(sessionInfo.session_id);

                var connection = new RelayConnection(_backendUrl, _relayUrl, _apiKey, _hardwareId, _useRelayTls);
                
                // Subscribe to events
                connection.ConnectionLost += async (sender, error) =>
                {
                    await HandleConnectionLost(viewModel, connection, error);
                };
                
                connection.Reconnected += (sender, args) =>
                {
                    HandleReconnected(viewModel);
                };
                
                connection.ActiveConnectionsChanged += (sender, count) =>
                {
                    HandleActiveConnectionsChanged(viewModel, count);
                };
                
                connection.ThroughputChanged += (sender, throughput) =>
                {
                    HandleThroughputChanged(viewModel, connection, throughput.upload, throughput.download);
                };
                
                connection.PingChanged += (sender, ping) =>
                {
                    HandlePingChanged(viewModel, ping.current, ping.average);
                };
                
                var (success, error) = await connection.ConnectToSession(sessionInfo.session_id, localPort);

                if (success)
                {
                    _activeConnections[sessionInfo.session_id] = connection;
                    
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnecting = false;
                        viewModel.IsConnectedLocal = true;
                        viewModel.LocalPort = localPort;
                    });
                    
                    Logging.Debug("SessionsWindow", "ConnectToSessionAsync", 
                        $"Connected to session {sessionInfo.session_id} on local port {localPort}");
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        viewModel.IsConnecting = false;
                        viewModel.IsConnectedLocal = false;
                        viewModel.LocalPort = 0;
                    });
                    
                    Logging.Error("SessionsWindow", "ConnectToSessionAsync", 
                        $"Failed to connect: {error}");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("SessionsWindow", "ConnectToSessionAsync", ex.ToString());
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    viewModel.IsConnecting = false;
                    viewModel.IsConnectedLocal = false;
                    viewModel.LocalPort = 0;
                });
            }
        }
        
        private async Task WaitForWindowClose(Window window)
        {
            var tcs = new TaskCompletionSource<bool>();
            
            EventHandler? closedHandler = null;
            closedHandler = (s, e) =>
            {
                window.Closed -= closedHandler;
                tcs.TrySetResult(true);
            };
            
            window.Closed += closedHandler;
            
            await tcs.Task;
        }
        
        /// <summary>
        /// Shows a warning dialog when attempting to connect to a session with active connections
        /// Returns true if user confirms, false if user cancels
        /// </summary>
        private async Task<bool> ShowActiveConnectionWarning(int activeConnections)
        {
            var tcs = new TaskCompletionSource<bool>();
            
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var warningWindow = new Window
                    {
                        Title = "Administrator Already Connected",
                        Width = 550,
                        Height = 320,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        CanResize = false,
                        ShowInTaskbar = false
                    };
                    
                    // Main container with padding
                    var mainPanel = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(25),
                        Spacing = 20,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                    };
                    
                    // Warning icon and title
                    var titlePanel = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 12,
                        Margin = new Avalonia.Thickness(0, 0, 0, 5)
                    };
                    
                    var iconText = new TextBlock
                    {
                        Text = "[WARNING]",
                        FontSize = 36,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    
                    var titleText = new TextBlock
                    {
                        Text = "Administrator Already Connected",
                        FontSize = 20,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    
                    titlePanel.Children.Add(iconText);
                    titlePanel.Children.Add(titleText);
                    
                    // Message
                    var messageText = new TextBlock
                    {
                        Text = $"Warning: Another administrator is already connected to this device.\n\n" +
                               "If you proceed, you will terminate the other admin's connection and take over the session.\n\n" +
                               "Effects:\n" +
                               "- The other admin's active session(s) for this device will be terminated immediately.\n" +
                               "- The other admin will be disconnected and need to reconnect.\n" +
                               "- You will gain exclusive access to the device.\n\n" +
                               "Do you want to disconnect the existing connection and connect now?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 14,
                        Margin = new Avalonia.Thickness(0, 0, 0, 10)
                    };
                    
                    // Button panel
                    var buttonPanel = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 12,
                        Margin = new Avalonia.Thickness(0, 15, 0, 0)
                    };
                    
                    var cancelButton = new Button
                    {
                        Content = "Cancel",
                        Width = 120,
                        Height = 36,
                        Padding = new Avalonia.Thickness(15, 8)
                    };
                    cancelButton.Classes.Add("secondary");
                    
                    var connectButton = new Button
                    {
                        Content = "Disconnect & Connect",
                        Width = 200,
                        Height = 36,
                        Padding = new Avalonia.Thickness(15, 8)
                    };
                    connectButton.Classes.Add("primary");
                    
                    cancelButton.Click += (s, e) =>
                    {
                        tcs.TrySetResult(false);
                        warningWindow.Close();
                    };
                    
                    connectButton.Click += (s, e) =>
                    {
                        tcs.TrySetResult(true);
                        warningWindow.Close();
                    };
                    
                    buttonPanel.Children.Add(cancelButton);
                    buttonPanel.Children.Add(connectButton);
                    
                    mainPanel.Children.Add(titlePanel);
                    mainPanel.Children.Add(messageText);
                    mainPanel.Children.Add(buttonPanel);
                    
                    // Use ScrollViewer to ensure content is always visible
                    var scrollViewer = new ScrollViewer
                    {
                        Content = mainPanel,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    };
                    
                    warningWindow.Content = scrollViewer;
                    
                    // Handle window close without button click (X button)
                    warningWindow.Closed += (s, e) =>
                    {
                        tcs.TrySetResult(false);
                    };
                    
                    await warningWindow.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Logging.Error("SessionsWindow", "ShowActiveConnectionWarning", ex.ToString());
                    tcs.TrySetResult(true); // On error, allow connection
                }
            });
            
            return await tcs.Task;
        }
    }

    /// <summary>
    /// ViewModel for session display with binding support
    /// </summary>
    public class SessionViewModel : INotifyPropertyChanged
    {
        private RelaySessionInfo _sessionInfo;
        private bool _isConnectedLocal;
        private int _localPort;
        private bool _isConnecting;
        private bool _hasLocalAppConnected;
        private int _activeConnectionCount;
        private long _uploadSpeed;
        private long _downloadSpeed;
        private long _totalUpload;
        private long _totalDownload;
        private long _currentPing;
        private long _averagePing;
        private bool _autoConnect;

        public RelaySessionInfo SessionInfo
        {
            get => _sessionInfo;
            set
            {
                _sessionInfo = value;
                OnPropertyChanged(nameof(SessionInfo));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(TargetDeviceDisplay));
                OnPropertyChanged(nameof(ConnectionStatus));
                OnPropertyChanged(nameof(OrganizationDisplay));
                OnPropertyChanged(nameof(HasOrganizationInfo));
                OnPropertyChanged(nameof(CanConnect));
                UpdateStatusProperties();
            }
        }

        public bool IsConnectedLocal
        {
            get => _isConnectedLocal;
            set
            {
                if (_isConnectedLocal != value)
                {
                    _isConnectedLocal = value;
                    OnPropertyChanged(nameof(IsConnectedLocal));
                    OnPropertyChanged(nameof(IsNotConnectedLocal));
                    OnPropertyChanged(nameof(ConnectionStatus));
                    OnPropertyChanged(nameof(LocalPortDisplay));
                    OnPropertyChanged(nameof(CanConnect));
                    UpdateStatusProperties();
                }
            }
        }

        public bool IsNotConnectedLocal => !_isConnectedLocal;

        public bool IsConnecting
        {
            get => _isConnecting;
            set
            {
                if (_isConnecting != value)
                {
                    _isConnecting = value;
                    OnPropertyChanged(nameof(IsConnecting));
                    OnPropertyChanged(nameof(CanConnect));
                    UpdateStatusProperties();
                }
            }
        }

        // User can only click "Connect" if device is ready (online and not already used by another admin)
        // Disable if already connecting, connected locally, or device is not ready
        public bool CanConnect => !_isConnectedLocal && !_isConnecting && 
            _sessionInfo?.display_status == "Ready to connect";

        public int LocalPort
        {
            get => _localPort;
            set
            {
                if (_localPort != value)
                {
                    _localPort = value;
                    OnPropertyChanged(nameof(LocalPort));
                    OnPropertyChanged(nameof(LocalPortDisplay));
                    OnPropertyChanged(nameof(ConnectionStatus));
                }
            }
        }
        
        public bool HasLocalAppConnected
        {
            get => _hasLocalAppConnected;
            set
            {
                if (_hasLocalAppConnected != value)
                {
                    _hasLocalAppConnected = value;
                    OnPropertyChanged(nameof(HasLocalAppConnected));
                    OnPropertyChanged(nameof(LocalAppConnectionStatus));
                    OnPropertyChanged(nameof(ConnectionStatus));
                    UpdateStatusProperties();
                }
            }
        }
        
        public int ActiveConnectionCount
        {
            get => _activeConnectionCount;
            set
            {
                if (_activeConnectionCount != value)
                {
                    _activeConnectionCount = value;
                    OnPropertyChanged(nameof(ActiveConnectionCount));
                    OnPropertyChanged(nameof(LocalAppConnectionStatus));
                }
            }
        }
        
        public long UploadSpeed
        {
            get => _uploadSpeed;
            set
            {
                if (_uploadSpeed != value)
                {
                    _uploadSpeed = value;
                    OnPropertyChanged(nameof(UploadSpeed));
                    OnPropertyChanged(nameof(UploadSpeedDisplay));
                    OnPropertyChanged(nameof(HasThroughput));
                }
            }
        }
        
        public long DownloadSpeed
        {
            get => _downloadSpeed;
            set
            {
                if (_downloadSpeed != value)
                {
                    _downloadSpeed = value;
                    OnPropertyChanged(nameof(DownloadSpeed));
                    OnPropertyChanged(nameof(DownloadSpeedDisplay));
                    OnPropertyChanged(nameof(HasThroughput));
                }
            }
        }
        
        public long TotalUpload
        {
            get => _totalUpload;
            set
            {
                if (_totalUpload != value)
                {
                    _totalUpload = value;
                    OnPropertyChanged(nameof(TotalUpload));
                    OnPropertyChanged(nameof(TotalUploadDisplay));
                }
            }
        }
        
        public long TotalDownload
        {
            get => _totalDownload;
            set
            {
                if (_totalDownload != value)
                {
                    _totalDownload = value;
                    OnPropertyChanged(nameof(TotalDownload));
                    OnPropertyChanged(nameof(TotalDownloadDisplay));
                }
            }
        }
        
        public long CurrentPing
        {
            get => _currentPing;
            set
            {
                if (_currentPing != value)
                {
                    _currentPing = value;
                    OnPropertyChanged(nameof(CurrentPing));
                    OnPropertyChanged(nameof(CurrentPingDisplay));
                    OnPropertyChanged(nameof(PingQuality));
                }
            }
        }
        
        public long AveragePing
        {
            get => _averagePing;
            set
            {
                if (_averagePing != value)
                {
                    _averagePing = value;
                    OnPropertyChanged(nameof(AveragePing));
                    OnPropertyChanged(nameof(AveragePingDisplay));
                }
            }
        }
        
        public bool AutoConnect
        {
            get => _autoConnect;
            set
            {
                if (_autoConnect != value)
                {
                    _autoConnect = value;
                    OnPropertyChanged(nameof(AutoConnect));
                    
                    // Trigger save when changed
                    AutoConnectChanged?.Invoke(this, value);
                }
            }
        }
        
        public event EventHandler<bool>? AutoConnectChanged;
        
        public bool HasThroughput => _uploadSpeed > 0 || _downloadSpeed > 0;

        public string LocalPortDisplay => IsConnectedLocal && LocalPort > 0 
            ? $"localhost:{LocalPort}" 
            : "Not connected";
        
        public string UploadSpeedDisplay => FormatBytes(UploadSpeed) + "/s";
        public string DownloadSpeedDisplay => FormatBytes(DownloadSpeed) + "/s";
        public string TotalUploadDisplay => FormatBytes(TotalUpload);
        public string TotalDownloadDisplay => FormatBytes(TotalDownload);
        
        public string CurrentPingDisplay => CurrentPing >= 9999 ? "N/A" : $"{CurrentPing} ms";
        public string AveragePingDisplay => AveragePing >= 9999 ? "N/A" : $"{AveragePing} ms";
        
        public string PingQuality
        {
            get
            {
                if (CurrentPing >= 9999) return "Offline";
                if (CurrentPing <= 50) return "Excellent";
                if (CurrentPing <= 100) return "Good";
                if (CurrentPing <= 200) return "Fair";
                return "Poor";
            }
        }
        
        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";
            
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            
            return $"{size:0.##} {sizes[order]}";
        }
        
        public string LocalAppConnectionStatus
        {
            get
            {
                if (!IsConnectedLocal)
                    return "";
                
                if (HasLocalAppConnected)
                    return $"{ActiveConnectionCount} app(s) connected";
                else
                    return "Ready - No apps connected yet";
            }
        }

        public string DisplayName => SessionInfo?.DisplayName ?? "Unknown";
        
        public string TargetDeviceDisplay => 
            !string.IsNullOrEmpty(SessionInfo?.target_device_name) 
                ? SessionInfo.target_device_name 
                : SessionInfo?.target_device_id ?? "Unknown";
        
        public string OrganizationDisplay => SessionInfo?.OrganizationInfo ?? "";
        
        public bool HasOrganizationInfo => !string.IsNullOrEmpty(OrganizationDisplay);
        
        // Status type properties for visual styling
        public bool IsStatusActive => IsConnectedLocal || (SessionInfo?.is_active == true && SessionInfo?.enabled == true);
        
        public bool IsStatusWaiting => !IsConnectedLocal && SessionInfo?.is_active == false && SessionInfo?.enabled == true && 
                                        (SessionInfo?.display_status == "Waiting for device" || SessionInfo?.display_status == "Device offline" || 
                                         (!SessionInfo.is_device_ready && string.IsNullOrEmpty(SessionInfo?.display_status)));
        
        public bool IsStatusDisabled => SessionInfo?.enabled == false && SessionInfo?.is_active == false;
        
        public bool IsStatusDisconnecting => SessionInfo?.enabled == false && SessionInfo?.is_active == true;
        
        // New status: Ready to Connect (Device online, session enabled but not active)
        public bool IsStatusReady => !IsConnectedLocal && SessionInfo?.enabled == true && SessionInfo?.is_active == false &&
                                     (SessionInfo?.display_status == "Ready to connect" || SessionInfo.is_device_ready);
        
        // Mutually exclusive visibility for status chips
        public bool ShowConnectingChip => IsConnecting;
        
        public bool ShowConnectedChip => !IsConnecting && IsConnectedLocal && HasLocalAppConnected;
        
        public bool ShowReadyChip => !IsConnecting && IsConnectedLocal && !HasLocalAppConnected;
        
        public bool ShowDisconnectingChip => !IsConnecting && !IsConnectedLocal && IsStatusDisconnecting;
        
        public bool ShowDisabledChip => !IsConnecting && !IsConnectedLocal && !IsStatusDisconnecting && IsStatusDisabled;
        
        // "Ready to Connect" chip (green) - Device is online and ready
        public bool ShowReadyToConnectChip => !IsConnecting && !IsConnectedLocal && !IsStatusDisconnecting && !IsStatusDisabled && IsStatusReady;
        
        // "Waiting" chip (orange) - Device is offline or not responding
        public bool ShowWaitingChip => !IsConnecting && !IsConnectedLocal && !IsStatusDisconnecting && !IsStatusDisabled && !IsStatusReady && IsStatusWaiting;
        
        public bool ShowActiveChip => !IsConnecting && !IsConnectedLocal && !IsStatusDisconnecting && !IsStatusDisabled && !IsStatusWaiting && !IsStatusReady && IsStatusActive;
        
        public string ConnectionStatus
        {
            get
            {
                if (IsConnecting)
                    return "Connecting...";
                if (IsConnectedLocal && LocalPort > 0)
                {
                    if (HasLocalAppConnected)
                        return $"Connected (Port {LocalPort}) - {ActiveConnectionCount} active";
                    else
                        return $"Ready (Port {LocalPort}) - Waiting for app";
                }
                
                // Use the status from SessionInfo if available
                if (SessionInfo != null)
                    return $"{SessionInfo.DisplayStatus}".Trim();
                
                return "Unknown";
            }
        }

        public SessionViewModel(RelaySessionInfo sessionInfo)
        {
            _sessionInfo = sessionInfo;
            _localPort = 0;
            _isConnectedLocal = false;
            _isConnecting = false;
            _hasLocalAppConnected = false;
            _activeConnectionCount = 0;
            _uploadSpeed = 0;
            _downloadSpeed = 0;
            _totalUpload = 0;
            _totalDownload = 0;
            _currentPing = 0;
            _averagePing = 0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        private void UpdateStatusProperties()
        {
            OnPropertyChanged(nameof(IsStatusActive));
            OnPropertyChanged(nameof(IsStatusWaiting));
            OnPropertyChanged(nameof(IsStatusDisabled));
            OnPropertyChanged(nameof(IsStatusDisconnecting));
            OnPropertyChanged(nameof(IsStatusReady)); // 🆕
            OnPropertyChanged(nameof(ShowConnectingChip));
            OnPropertyChanged(nameof(ShowConnectedChip));
            OnPropertyChanged(nameof(ShowReadyChip));
            OnPropertyChanged(nameof(ShowDisconnectingChip));
            OnPropertyChanged(nameof(ShowDisabledChip));
            OnPropertyChanged(nameof(ShowReadyToConnectChip)); // 🆕
            OnPropertyChanged(nameof(ShowWaitingChip));
            OnPropertyChanged(nameof(ShowActiveChip));
        }
    }
}

