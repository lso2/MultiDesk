using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDesk.Models;
using MultiDesk.Services;

namespace MultiDesk.UI
{
    /// <summary>
    /// Cross-desktop switcher. Lists every window on every desktop and activates the chosen one through
    /// the same hardened routine the bar uses, as a correct one-press replacement for the OS switcher.
    /// </summary>
    public partial class SwitcherWindow : Window
    {
        public sealed class SwitcherItem
        {
            public ImageSource Icon { get; set; }
            public string Title { get; set; }
            public string Desktop { get; set; }
            public WindowModel Model { get; set; }
        }

        private static SwitcherWindow _instance;

        public SwitcherWindow()
        {
            InitializeComponent();
            List.MouseDoubleClick += (s, e) => ActivateSelected();
            List.KeyDown += OnListKey;
            KeyDown += OnKey;
            Deactivated += (s, e) => Close();
            Closed += (s, e) => { if (ReferenceEquals(_instance, this)) _instance = null; };
        }

        public static void ShowSingleton()
        {
            if (_instance != null) { _instance.Activate(); return; }
            var w = new SwitcherWindow();
            _instance = w;
            w.Populate();
            w.Show();
            w.Activate();
            if (w.List.Items.Count > 0)
            {
                w.List.SelectedIndex = 0;
                var first = w.List.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                if (first != null) first.Focus();
                else w.List.Focus();
            }
        }

        private void Populate()
        {
            var items = new List<SwitcherItem>();
            foreach (var d in App.Desktops.Desktops)
                foreach (var w in d.Windows)
                    items.Add(new SwitcherItem { Icon = w.Icon, Title = w.Title, Desktop = d.Name, Model = w });
            List.ItemsSource = items;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { ActivateSelected(); e.Handled = true; }
        }

        private void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ActivateSelected(); e.Handled = true; }
        }

        private void ActivateSelected()
        {
            var item = List.SelectedItem as SwitcherItem;
            if (item == null || item.Model == null) return;
            Close();
            WindowActions.Activate(item.Model);
        }
    }
}
