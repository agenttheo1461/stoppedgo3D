using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════
//  WikiPage — one wiki article. Create these via the asset menu
//  (Headway > Wiki > Wiki Page) the same way you'd create any other
//  ScriptableObject data asset -- one asset per page, filled in in the
//  Inspector like a real CMS entry: title, sections (heading + body text +
//  optional image + optional caption), and links to other WikiPage assets
//  so pages can reference each other ("See Also" and inline links).
//
//  Crystal Bay Times is just its own WikiCategory -- a CBT "page" IS a wiki
//  page, it just carries a byline/date and renders with a newspaper-style
//  header instead of a plain article header. No separate asset type needed.
// ═══════════════════════════════════════════════════════════════════════════
[CreateAssetMenu(fileName = "New Wiki Page", menuName = "Headway/Wiki/Wiki Page", order = 1)]
public class WikiPage : ScriptableObject
{
    public enum WikiCategory { Fleet, Routes, Lore, CrystalBayTimes }

    [Header("Identity")]
    public string pageTitle = "Untitled Page";

    [Tooltip("Stable ID used internally for links and browser history. Auto-generated once and then left alone -- you can freely rename pageTitle later without breaking any links that point at this page.")]
    public string pageId;

    public WikiCategory category = WikiCategory.Fleet;

    [Tooltip("Short one-line description shown in sidebar listings and search results, under the title.")]
    [TextArea(1, 2)]
    public string blurb;

    [Header("Crystal Bay Times only")]
    [Tooltip("Byline date shown under the headline. Only rendered for CrystalBayTimes category pages.")]
    public string publishDate;
    [Tooltip("Reporter/byline name. Only rendered for CrystalBayTimes category pages.")]
    public string byline;

    [Header("Content")]
    [Tooltip("Optional large image shown at the top of the page, under the title.")]
    public Texture2D headerImage;

    public List<WikiSection> sections = new List<WikiSection>();

    [Header("See Also")]
    [Tooltip("Related pages shown as a link list at the bottom of the article.")]
    public List<WikiPage> relatedPages = new List<WikiPage>();

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(pageId))
            pageId = Guid.NewGuid().ToString("N").Substring(0, 10);
    }
}

// One block of content within a page: an optional heading, body text, and
// an optional image with caption. A page is just a list of these stacked
// top to bottom -- add as many as you need per article.
[Serializable]
public class WikiSection
{
    [Tooltip("Leave blank for an intro paragraph with no sub-heading.")]
    public string heading;

    [Tooltip("Body text. Two inline markers are supported, written directly into this text:\n\n" +
             "  [[Exact Page Title]] -- a clickable link, anywhere in the text. " +
             "Resolved by title match against every page in the database.\n\n" +
             "  {{#RRGGBB}} or {{#RRGGBB|Label}} -- a small colored swatch " +
             "square, e.g. 'FUEL COLOR: {{#2E86AB}}' or 'Old livery: " +
             "{{#F2C14E|Yellow nose}}'. Put your own label text before the " +
             "marker as plain text ('FUEL COLOR:') -- the marker itself just " +
             "drops the colored square (and optional caption) at that point.\n\n" +
             "A line containing only swatches renders as one row with the " +
             "square genuinely next to the text. A line containing a link " +
             "renders the link as its own short line right after.")]
    [TextArea(3, 14)]
    public string body;

    [Tooltip("Optional image shown under this section's body text.")]
    public Texture2D image;

    [Tooltip("Optional caption shown under the image, smaller/italic style.")]
    public string imageCaption;

    [Tooltip("Optional charts shown under this section's image (bar or proportion-bar). Good for fleet counts by series, livery mix, depot allocation, etc.")]
    public List<WikiChart> charts = new List<WikiChart>();

    [Tooltip("Optional row of link buttons at the end of this section, e.g. 'See: XN40 Conversion Program'. Use this when you want a DIFFERENT display label than the target's title -- for [[Page Title]]-style links inline in your paragraph, just type them directly into the body text above instead.")]
    public List<WikiLink> inlineLinks = new List<WikiLink>();
}

// A single clickable link to another wiki page, with its own display label
// (doesn't have to match the target page's title -- e.g. "the 2000 Series"
// linking to a page titled "XNE40 Conversion Program").
[Serializable]
public class WikiLink
{
    public string label;
    public WikiPage target;
}

// ═══════════════════════════════════════════════════════════════════════════
//  WikiChart — a simple bar or proportion (stacked-percentage) chart you can
//  drop into any section. Fill in entries by hand (label + value + color) --
//  e.g. one entry per fleet series with its bus count, or one entry per
//  livery with its share of the fleet. Nothing here reads live game data;
//  it's authored the same way the rest of a wiki page is authored. If you
//  later want a chart to track real FleetRosterData counts instead of typed-
//  in numbers, that's a separate small helper script that populates a
//  WikiChart's entries at build/edit time -- ask and I'll wire that up once
//  I can see FleetRosterData's actual shape.
// ═══════════════════════════════════════════════════════════════════════════
public enum WikiChartType { Bar, ProportionBar }

[Serializable]
public class WikiChart
{
    public string title;
    public WikiChartType chartType = WikiChartType.Bar;

    [Tooltip("Bar chart only -- optional unit shown after each value, e.g. 'buses'.")]
    public string valueSuffix;

    public List<WikiChartEntry> entries = new List<WikiChartEntry>();
}

[Serializable]
public class WikiChartEntry
{
    public string label;
    public float value;
    public Color color = new Color(0.30f, 0.65f, 0.95f, 1f);
}