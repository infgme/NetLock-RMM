using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Global.Helper;

namespace NetLock_RMM_Relay_App.Windows
{
    public partial class SetPasswordWindow : Window
    {
        private TextBox? _passwordTextBox;
        private TextBox? _confirmPasswordTextBox;
        private Button? _saveButton;
        private TextBlock? _errorTextBlock;
        
        public string? Password { get; private set; }
        public bool PasswordSet { get; private set; }

        public SetPasswordWindow()
        {
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
            _confirmPasswordTextBox = this.FindControl<TextBox>("ConfirmPasswordTextBox");
            _saveButton = this.FindControl<Button>("SaveButton");
            _errorTextBlock = this.FindControl<TextBlock>("ErrorTextBlock");
            
            if (_passwordTextBox != null)
                _passwordTextBox.Focus();
        }

        private void SaveButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (_passwordTextBox == null || _confirmPasswordTextBox == null)
                    return;
                
                string password = _passwordTextBox.Text ?? "";
                string confirm = _confirmPasswordTextBox.Text ?? "";
                
                // Validate
                if (string.IsNullOrWhiteSpace(password))
                {
                    ShowError("Please enter a password");
                    return;
                }
                
                if (password.Length < 6)
                {
                    ShowError("Password must be at least 6 characters");
                    return;
                }
                
                if (password != confirm)
                {
                    ShowError("Passwords do not match");
                    return;
                }
                
                Password = password;
                PasswordSet = true;
                
                Logging.Info("SetPasswordWindow", "SaveButton_Click", "Password set successfully");
                Close();
            }
            catch (Exception ex)
            {
                Logging.Error("SetPasswordWindow", "SaveButton_Click", ex.ToString());
                ShowError($"Error: {ex.Message}");
            }
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e)
        {
            PasswordSet = false;
            Close();
        }
        
        private void ShowError(string message)
        {
            if (_errorTextBlock != null)
            {
                _errorTextBlock.Text = $"Error: {message}";
                _errorTextBlock.IsVisible = true;
            }
        }
    }
}

