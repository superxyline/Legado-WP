using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 替换净化规则管理：增删改查、启停、排序（上移/下移）。
    /// 列表项包装 RuleItem（Name/Preview/Enabled）供 XAML 绑定。
    /// </summary>
    public sealed partial class ReplacePage : Page
    {
        /// <summary>绑定项（编辑对话框改动回写 Rule 实例）。</summary>
        private class RuleItem
        {
            public ReplaceRule Rule;
            public string Name => Rule.Name;
            public string Preview => (Rule.UseRegex ? "[正则] " : "") + Rule.Pattern + " → " + (Rule.Replacement.Length > 0 ? Rule.Replacement : "（删除）")
                + (string.IsNullOrEmpty(Rule.Scope) ? "" : "  @ " + Rule.Scope);
            public bool Enabled
            {
                get { return Rule.Enabled; }
                set { Rule.Enabled = value; }
            }
        }

        private List<ReplaceRule> _rules = new List<ReplaceRule>();

        public ReplacePage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await ReloadAsync();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            _rules = await Storage.LoadReplaceRulesAsync();
            _rules = _rules.OrderBy(r => r.SortOrder).ToList();
            RuleList.ItemsSource = _rules.Select((r, i) => new RuleItem { Rule = r }).ToList();
            var empty = _rules.Count == 0;
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            RuleList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void OnToggle(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            var item = cb?.DataContext as RuleItem;
            if (item == null) return;
            await Storage.SaveReplaceRulesAsync(_rules);
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var item = btn?.DataContext as RuleItem;
            if (item == null) return;
            var dlg = new ContentDialog
            {
                Title = "删除规则",
                Content = "删除「" + item.Rule.Name + "」？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                _rules.Remove(item.Rule);
                await Storage.SaveReplaceRulesAsync(_rules);
                await ReloadAsync();
            }
        }

        private async void OnAddClick(object sender, RoutedEventArgs e)
        {
            var rule = new ReplaceRule { Name = "新规则", SortOrder = _rules.Count };
            if (await EditDialogAsync(rule))
            {
                _rules.Add(rule);
                await Storage.SaveReplaceRulesAsync(_rules);
                await ReloadAsync();
            }
        }

        private async void OnEditClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as RuleItem;
            if (item == null) return;
            var copy = new ReplaceRule
            {
                Name = item.Rule.Name, Pattern = item.Rule.Pattern, Replacement = item.Rule.Replacement,
                Scope = item.Rule.Scope, UseRegex = item.Rule.UseRegex, Enabled = item.Rule.Enabled,
                SortOrder = item.Rule.SortOrder
            };
            if (await EditDialogAsync(copy))
            {
                var idx = _rules.IndexOf(item.Rule);
                _rules[idx] = copy;
                await Storage.SaveReplaceRulesAsync(_rules);
                await ReloadAsync();
            }
        }

        /// <summary>新增/编辑对话框。ContentDialog 里放 StackPanel 表单。</summary>
        private async System.Threading.Tasks.Task<bool> EditDialogAsync(ReplaceRule rule)
        {
            var name = new TextBox { Text = rule.Name, PlaceholderText = "规则名" };
            var pattern = new TextBox { Text = rule.Pattern, PlaceholderText = "查找内容（或正则表达式）" };
            var replacement = new TextBox { Text = rule.Replacement, PlaceholderText = "替换为（留空 = 删除匹配内容）" };
            var scope = new TextBox { Text = rule.Scope, PlaceholderText = "作用域：书名/书源片段，逗号分隔，空=全部" };
            var useRegex = new CheckBox { Content = "使用正则表达式", IsChecked = rule.UseRegex };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(name);
            panel.Children.Add(pattern);
            panel.Children.Add(replacement);
            panel.Children.Add(scope);
            panel.Children.Add(useRegex);

            var dlg = new ContentDialog
            {
                Title = "净化规则",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

            rule.Name = string.IsNullOrWhiteSpace(name.Text) ? "未命名" : name.Text.Trim();
            rule.Pattern = pattern.Text ?? "";
            rule.Replacement = replacement.Text ?? "";
            rule.Scope = scope.Text?.Trim() ?? "";
            rule.UseRegex = useRegex.IsChecked == true;
            if (rule.Pattern.Length == 0)
            {
                var warn = new ContentDialog { Title = "查找内容为空", Content = "未保存", CloseButtonText = "好" };
                await warn.ShowAsync();
                return false;
            }
            return true;
        }
    }
}
