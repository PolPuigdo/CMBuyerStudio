using System.Windows;
using System.Windows.Controls;

namespace CMBuyerStudio.Desktop.Helpers;

public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBinding),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxBinding),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBinding),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject dependencyObject)
        => (string)dependencyObject.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject dependencyObject, string value)
        => dependencyObject.SetValue(BoundPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject dependencyObject)
        => (bool)dependencyObject.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject dependencyObject, bool value)
        => dependencyObject.SetValue(BindPasswordProperty, value);

    private static bool GetIsUpdating(DependencyObject dependencyObject)
        => (bool)dependencyObject.GetValue(IsUpdatingProperty);

    private static void SetIsUpdating(DependencyObject dependencyObject, bool value)
        => dependencyObject.SetValue(IsUpdatingProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= HandlePasswordChanged;

        if (!GetIsUpdating(passwordBox))
        {
            passwordBox.Password = eventArgs.NewValue as string ?? string.Empty;
        }

        passwordBox.PasswordChanged += HandlePasswordChanged;
    }

    private static void OnBindPasswordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        var wasBound = (bool)eventArgs.OldValue;
        var shouldBind = (bool)eventArgs.NewValue;

        if (wasBound)
        {
            passwordBox.PasswordChanged -= HandlePasswordChanged;
        }

        if (shouldBind)
        {
            passwordBox.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetIsUpdating(passwordBox, false);
    }
}
