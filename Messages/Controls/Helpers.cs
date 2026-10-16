using Messages.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Messages.Controls;

public sealed class ThreadTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DayTemplate { get; set; }
    public DataTemplate? IncomingTemplate { get; set; }
    public DataTemplate? OutgoingTemplate { get; set; }
    public DataTemplate? SystemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        DayItem => DayTemplate,
        SystemItem => SystemTemplate,
        MessageItem { Model.IsFromMe: true } => OutgoingTemplate,
        MessageItem => IncomingTemplate,
        _ => base.SelectTemplateCore(item)
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public sealed class InboxTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ConversationTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? HitTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        ConversationItem => ConversationTemplate,
        SectionHeaderItem => HeaderTemplate,
        SearchHitItem => HitTemplate,
        _ => base.SelectTemplateCore(item)
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Renders message text with clickable web links.</summary>
public static class Linkify
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Linkify), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        var text = e.NewValue as string ?? "";
        block.Inlines.Clear();
        var index = 0;
        foreach (System.Text.RegularExpressions.Match match in Ui.UrlRegex().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':');
            if (match.Index > index) block.Inlines.Add(new Run { Text = text[index..match.Index] });
            var target = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink { NavigateUri = uri, UnderlineStyle = UnderlineStyle.Single, Foreground = block.Foreground };
                link.Inlines.Add(new Run { Text = url });
                block.Inlines.Add(link);
            }
            else block.Inlines.Add(new Run { Text = url });
            index = match.Index + url.Length;
        }
        if (index < text.Length) block.Inlines.Add(new Run { Text = text[index..] });
    }
}
