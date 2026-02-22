using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Global.Helper;
using NetLock_RMM_Relay_App.Global.Config;

namespace NetLock_RMM_Relay_App.Windows
{
    public partial class UnlockWindow : Window
    {
        private TextBox? _passwordTextBox;
        private Button? _unlockButton;
        private TextBlock? _errorTextBlock;
        
        private readonly string _passwordHash;
        private bool _unlocked = false;

        public bool Unlocked => _unlocked;

        public UnlockWindow(string passwordHash)
        {
            _passwordHash = passwordHash;
            
            InitializeComponent();
            InitializeControls();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void InitializeControls()
        {
            _passwordTextBox = this.FindControl<TextBox>("PasswordTextBox");
            _unlockButton = this.FindControl<Button>("UnlockButton");
            _errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock");
            
            // Focus password field
            if (_passwordTextBox != null)
            {
                _passwordTextBox.Focus();
                _passwordTextBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Enter)
                    {
                        UnlockButton_Click(s, new RoutedEventArgs());
                    }
                };
            }
        }

        private void UnlockButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (_passwordTextBox == null || string.IsNullOrWhiteSpace(_passwordTextBox.Text))
                {
                    if (_errorTextBlock != null)
                    {
                        _errorTextBlock.Text = "Please enter a password";
                        _errorTextBlock.IsVisible = true;
                    }
                    return;
                }

                string password = _passwordTextBox.Text;
                
                if (SecureConfig.VerifyPassword(password, _passwordHash))
                {
                    _unlocked = true;
                    Logging.Info("UnlockWindow", "UnlockButton_Click", "Window unlocked successfully");
                    Close();
                }
                else
                {
                    if (_errorTextBlock != null)
                    {
                        _errorTextBlock.Text = "Invalid password";
                        _errorTextBlock.IsVisible = true;
                    }
                    
                    if (_passwordTextBox != null)
                    {
                        _passwordTextBox.Text = "";
                        _passwordTextBox.Focus();
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("UnlockWindow", "UnlockButton_Click", ex.ToString());
                if (_errorTextBlock != null)
                {
                    _errorTextBlock.Text = $"Error: {ex.Message}";
                    _errorTextBlock.IsVisible = true;
                }
            }
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e)
        {
            _unlocked = false;
            Close();
        }
    }
}

