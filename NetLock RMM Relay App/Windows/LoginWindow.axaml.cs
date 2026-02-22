using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Global.Helper;
using NetLock_RMM_Relay_App.Global.Config;
using NetLock_RMM_Relay_App.RelayClient;

namespace NetLock_RMM_Relay_App.Windows
{
    public partial class LoginWindow : Window
    {
        private TextBox? _backendUrlTextBox;
        private TextBox? _relayUrlTextBox;
        private TextBox? _apiKeyTextBox;
        private TextBox? _hardwareIdTextBox;
        private TextBox? _passwordTextBox;
        private TextBox? _firstTimePasswordTextBox;
        private TextBox? _firstTimeConfirmPasswordTextBox;
        private Button? _loginButton;
        private Button? _resetButton;
        private Border? _statusBorder;
        private TextBlock? _statusTextBlock;
        private Border? _errorBorder;
        private TextBlock? _errorTextBlock;
        private StackPanel? _firstTimeSetupPanel;
        private StackPanel? _passwordLoginPanel;
        private bool _isFirstTimeSetup = true;
        public LoginWindow()
        {
            InitializeComponent();
            InitializeControls();
            CheckLoginMode();
        }
        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        private void InitializeControls()
        {
            _backendUrlTextBox = this.FindControl<TextBox>("BackendUrlTextBox");
            _relayUrlTextBox = this.FindControl<TextBox>("RelayUrlTextBox");
            _apiKeyTextBox = this.FindControl<TextBox>("ApiKeyTextBox");
            _hardwareIdTextBox = this.FindControl<TextBox>("HardwareIdTextBox");
            _passwordTextBox = this.FindControl<TextBox>("PasswordTextBox");
            _firstTimePasswordTextBox = this.FindControl<TextBox>("FirstTimePasswordTextBox");
            _firstTimeConfirmPasswordTextBox = this.FindControl<TextBox>("FirstTimeConfirmPasswordTextBox");
            _loginButton = this.FindControl<Button>("LoginButton");
            _resetButton = this.FindControl<Button>("ResetButton");
            _statusBorder = this.FindControl<Border>("StatusBorder");
            _statusTextBlock = this.FindControl<TextBlock>("StatusTextBlock");
            _errorBorder = this.FindControl<Border>("ErrorBorder");
            _errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock");
            _firstTimeSetupPanel = this.FindControl<StackPanel>("FirstTimeSetupPanel");
            _passwordLoginPanel = this.FindControl<StackPanel>("PasswordLoginPanel");
            // Set hardware ID
            if (_hardwareIdTextBox != null)
                _hardwareIdTextBox.Text = _x101.HWID_System.ENGINE.HW_UID;
        }
        private void CheckLoginMode()
        {
            try
            {
                _isFirstTimeSetup = !SecureConfig.ConfigExists();
                if (_firstTimeSetupPanel != null)
                    _firstTimeSetupPanel.IsVisible = _isFirstTimeSetup;
                if (_passwordLoginPanel != null)
                    _passwordLoginPanel.IsVisible = !_isFirstTimeSetup;
                if (_loginButton != null)
                {
                    _loginButton.Content = _isFirstTimeSetup 
                        ? "Save & Connect" 
                        : "Login";
                }
                Logging.Debug("LoginWindow", "CheckLoginMode", 
                    $"Login mode: {(_isFirstTimeSetup ? "First Time Setup" : "Password Login")}");
            }
            catch (Exception ex)
            {
                Logging.Error("LoginWindow", "CheckLoginMode", ex.ToString());
            }
        }
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Hide previous messages
                if (_statusBorder != null)
                    _statusBorder.IsVisible = false;
                if (_errorBorder != null)
                    _errorBorder.IsVisible = false;
                if (_isFirstTimeSetup)
                {
                    await HandleFirstTimeSetup();
                }
                else
                {
                    await HandlePasswordLogin();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("LoginWindow", "LoginButton_Click", ex.ToString());
                ShowError($"Error: {ex.Message}");
                ResetLoginButton();
            }
        }
        private async System.Threading.Tasks.Task HandleFirstTimeSetup()
        {
            // Validate all fields
            string backendUrl = _backendUrlTextBox?.Text?.Trim() ?? "";
            string relayUrl = _relayUrlTextBox?.Text?.Trim() ?? "";
            string apiKey = _apiKeyTextBox?.Text?.Trim() ?? "";
            string hardwareId = _hardwareIdTextBox?.Text?.Trim() ?? "";
            string password = _firstTimePasswordTextBox?.Text?.Trim() ?? "";
            string confirmPassword = _firstTimeConfirmPasswordTextBox?.Text?.Trim() ?? "";
            
            if (string.IsNullOrEmpty(backendUrl))
            {
                ShowError("Please enter a backend server URL");
                return;
            }
            if (string.IsNullOrEmpty(relayUrl))
            {
                ShowError("Please enter a relay URL");
                return;
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                ShowError("Please enter an API key");
                return;
            }
            if (string.IsNullOrEmpty(hardwareId))
            {
                ShowError("Hardware ID is not available");
                return;
            }
            if (string.IsNullOrEmpty(password))
            {
                ShowError("Password is required to secure your credentials");
                return;
            }
            if (password.Length < 6)
            {
                ShowError("Password must be at least 6 characters long");
                return;
            }
            if (password != confirmPassword)
            {
                ShowError("Passwords do not match");
                return;
            }
            // Disable login button
            if (_loginButton != null)
            {
                _loginButton.IsEnabled = false;
                _loginButton.Content = "Connecting...";
            }
            
            // Test connection to backend
            var apiClient = new RelayApiClient(backendUrl, apiKey, hardwareId);
            var (testSuccess, testError) = await apiClient.TestConnection();
            
            if (!testSuccess)
            {
                ShowError("Connection failed: " + (testError ?? "Unknown error"));
                ResetLoginButton();
                return;
            }
            
            // Rufe Server Public Key ab (für TOFU und E2EE-Identitätsverifikation)
            if (_loginButton != null)
                _loginButton.Content = "Verifying server identity...";
            
            var (keySuccess, serverPublicKey, serverFingerprint, keyError) = await apiClient.GetServerPublicKey();
            
            if (!keySuccess || string.IsNullOrEmpty(serverPublicKey) || string.IsNullOrEmpty(serverFingerprint))
            {
                Logging.Error("LoginWindow", "HandleFirstTimeSetup", 
                    $"Failed to get server public key: {keyError}");
                ShowError("[SECURITY] Could not verify server identity: " + (keyError ?? "Unknown error"));
                ResetLoginButton();
                return;
            }
            
            // Verify or store server fingerprint (TOFU)
            var (isNewServer, isValid, storedFingerprint) = 
                ServerTrustStore.VerifyOrStoreFingerprint(backendUrl, serverPublicKey, serverFingerprint);
            
            if (!isValid)
            {
                // MITM erkannt!
                Logging.Error("LoginWindow", "HandleFirstTimeSetup",
                    "[SECURITY] SERVER FINGERPRINT MISMATCH!\nExpected: {storedFingerprint}\nReceived: {serverFingerprint}");
                    
                ShowError("[SECURITY] SECURITY WARNING!\n\nServer fingerprint has changed!\n\n" +
                          "This could indicate a Man-in-the-Middle attack.\n\n" +
                          $"Expected: {storedFingerprint}\n" +
                          $"Received: {serverFingerprint}\n\n" +
                          "Connection refused.");
                ResetLoginButton();
                return;
            }
            
            if (isNewServer)
            {
                Logging.Info("LoginWindow", "HandleFirstTimeSetup",
                    $"New server added to trust store (TOFU)\nFingerprint: {serverFingerprint}");
                ShowStatus("[OK] Server identity verified (first contact)\nFingerprint: {serverFingerprint}");
                await System.Threading.Tasks.Task.Delay(1500);
            }
            else
            {
                Logging.Info("LoginWindow", "HandleFirstTimeSetup",
                    $"Server identity verified\nFingerprint: {serverFingerprint}");
                ShowStatus("[OK] Server identity verified");
                await System.Threading.Tasks.Task.Delay(500);
            }
            
            if (_loginButton != null)
                _loginButton.Content = "Authenticating...";
            
            // Register the relay client with backend
            var (registerSuccess, registerError) = await apiClient.RegisterRelayClient();
            if (!registerSuccess)
            {
                ShowError("Authentication failed: " + (registerError ?? "Unknown error"));
                ResetLoginButton();
                return;
            }
            // Save encrypted config
            var config = new SecureConfig.ConfigData
            {
                BackendUrl = backendUrl,
                RelayUrl = relayUrl,
                ApiKey = apiKey,
                HardwareId = hardwareId,
                PasswordHash = SecureConfig.HashPassword(password),
                UsePasswordAuth = true,
                UseRelayTls = false // Default to false for new configs
            };
            if (!SecureConfig.SaveConfig(config, password))
            {
                ShowError("Failed to save configuration");
                ResetLoginButton();
                return;
            }
            
            // Set encryption password for Application_Settings
            Application_Settings.Handler.SetEncryptionPassword(password);
            
            ShowStatus("Connected successfully! Password set.");
            // Wait a moment then open sessions window
            await System.Threading.Tasks.Task.Delay(500);
            var sessionsWindow = new SessionsWindow(backendUrl, relayUrl, apiKey, hardwareId, password, config.UseRelayTls);
            sessionsWindow.Show();
            Close();
        }
        private async System.Threading.Tasks.Task HandlePasswordLogin()
        {
            string password = _passwordTextBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(password))
            {
                ShowError("Please enter your password");
                return;
            }
            // Disable login button
            if (_loginButton != null)
            {
                _loginButton.IsEnabled = false;
                _loginButton.Content = "Decrypting...";
            }
            // Load and decrypt config
            var (success, config, error) = SecureConfig.LoadConfig(password);
            if (!success || config == null)
            {
                ShowError("Invalid password");
                ResetLoginButton();
                return;
            }
            if (_loginButton != null)
                _loginButton.Content = "Connecting...";
            
            string backendUrl = config.BackendUrl;
            string relayUrl = config.RelayUrl;
            string apiKey = config.ApiKey;
            string hardwareId = config.HardwareId;
            
            // Verify server identity (TOFU)
            if (_loginButton != null)
                _loginButton.Content = "Verifying server...";
            
            var apiClient = new RelayApiClient(backendUrl, apiKey, hardwareId);
            var (keySuccess, serverPublicKey, serverFingerprint, keyError) = await apiClient.GetServerPublicKey();
            
            if (keySuccess && !string.IsNullOrEmpty(serverPublicKey) && !string.IsNullOrEmpty(serverFingerprint))
            {
                var (isNewServer, isValid, storedFingerprint) = 
                    ServerTrustStore.VerifyOrStoreFingerprint(backendUrl, serverPublicKey, serverFingerprint);
                
                if (!isValid)
                {
                    // MITM erkannt!
                    Logging.Error("LoginWindow", "HandlePasswordLogin",
                        "[SECURITY] SERVER FINGERPRINT MISMATCH!\nExpected: {storedFingerprint}\nReceived: {serverFingerprint}");
                        
                    ShowError("[SECURITY] SECURITY WARNING!\n\nServer fingerprint has changed!\n\n" +
                              "This could indicate a Man-in-the-Middle attack.\n\n" +
                              $"Expected: {storedFingerprint}\n" +
                              $"Received: {serverFingerprint}\n\n" +
                              "Connection refused.");
                    ResetLoginButton();
                    return;
                }
                
                if (isNewServer)
                {
                    Logging.Info("LoginWindow", "HandlePasswordLogin",
                        $"Server fingerprint stored (TOFU): {serverFingerprint}");
                }
            }
            else
            {
                Logging.Error("LoginWindow", "HandlePasswordLogin",
                    $"Warning: Could not verify server identity: {keyError}");
            }
            
            if (_loginButton != null)
                _loginButton.Content = "Connecting...";
            
            // Test connection to backend
            var (testSuccess, testError) = await apiClient.TestConnection();
            if (!testSuccess)
            {
                ShowError("Connection failed: " + (testError ?? "Unknown error"));
                ResetLoginButton();
                return;
            }
            // Register the relay client with backend
            var (registerSuccess, registerError) = await apiClient.RegisterRelayClient();
            if (!registerSuccess)
            {
                ShowError("Authentication failed: " + (registerError ?? "Unknown error"));
                ResetLoginButton();
                return;
            }
            
            // Set encryption password for Application_Settings
            Application_Settings.Handler.SetEncryptionPassword(password);
            
            ShowStatus("Connected successfully!");
            // Wait a moment then open sessions window
            await System.Threading.Tasks.Task.Delay(500);
            var sessionsWindow = new SessionsWindow(backendUrl, relayUrl, apiKey, hardwareId, password, config.UseRelayTls);
            sessionsWindow.Show();
            Close();
        }
        private async void ResetButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var result = await ShowConfirmDialog(
                    "Reset Configuration",
                    "This will delete all saved credentials and session data. You will need to set up everything again.\n\nAre you sure?");
                if (result)
                {
                    SecureConfig.DeleteConfig();
                    ServerTrustStore.ClearTrustStore(); // Delete trusted servers as well
                    Logging.Info("LoginWindow", "ResetButton_Click", "Configuration and trust store reset by user");
                    ShowStatus("Configuration reset. Please set up again.");
                    await System.Threading.Tasks.Task.Delay(1000);
                    // Switch to first time setup mode
                    _isFirstTimeSetup = true;
                    CheckLoginMode();
                    // Clear all fields
                    if (_backendUrlTextBox != null)
                        _backendUrlTextBox.Text = "http://";
                    if (_apiKeyTextBox != null)
                        _apiKeyTextBox.Text = "";
                    if (_firstTimePasswordTextBox != null)
                        _firstTimePasswordTextBox.Text = "";
                    if (_firstTimeConfirmPasswordTextBox != null)
                        _firstTimeConfirmPasswordTextBox.Text = "";
                }
            }
            catch (Exception ex)
            {
                Logging.Error("LoginWindow", "ResetButton_Click", ex.ToString());
                ShowError($"Error: {ex.Message}");
            }
        }
        private async System.Threading.Tasks.Task<bool> ShowConfirmDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };
            var panel = new StackPanel
            {
                Margin = new Avalonia.Thickness(30)
            };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 14,
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            });
            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 10
            };
            bool result = false;
            var yesButton = new Button
            {
                Content = "Yes, Reset",
                Width = 120,
                Background = Avalonia.Media.Brushes.Red,
                Foreground = Avalonia.Media.Brushes.White
            };
            yesButton.Click += (s, e) => { result = true; dialog.Close(); };
            var noButton = new Button
            {
                Content = "Cancel",
                Width = 120
            };
            noButton.Click += (s, e) => { result = false; dialog.Close(); };
            buttonPanel.Children.Add(noButton);
            buttonPanel.Children.Add(yesButton);
            panel.Children.Add(buttonPanel);
            dialog.Content = panel;
            await dialog.ShowDialog(this);
            return result;
        }
        private void ResetLoginButton()
        {
            if (_loginButton != null)
            {
                _loginButton.IsEnabled = true;
                _loginButton.Content = _isFirstTimeSetup 
                    ? "Register" 
                    : "Login";
            }
        }
        private void ShowError(string message)
        {
            if (_errorBorder != null)
                _errorBorder.IsVisible = true;
            if (_errorTextBlock != null)
                _errorTextBlock.Text = message;
        }
        private void ShowStatus(string message)
        {
            if (_statusBorder != null)
                _statusBorder.IsVisible = true;
            if (_statusTextBlock != null)
                _statusTextBlock.Text = message;
        }
    }
}
