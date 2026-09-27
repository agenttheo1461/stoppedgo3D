using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════
//  WikiDatabase — one asset (Headway > Wiki > Wiki Database) that just holds
//  a list of every WikiPage in the game. Drag every WikiPage asset you create
//  into allPages here; WikiUIController reads from this single database, so
//  adding a new page is: create the WikiPage asset, fill it in, drag it into
//  this list. Nothing else needs to change.
// ═══════════════════════════════════════════════════════════════════════════
[CreateAssetMenu(fileName = "WikiDatabase", menuName = "Headway/Wiki/Wiki Database", order = 0)]
public class WikiDatabase : ScriptableObject
{
    public List<WikiPage> allPages = new List<WikiPage>();

    [Tooltip("Optional -- the page shown when the wiki is first opened. If left blank, a simple built-in landing screen is shown instead.")]
    public WikiPage homePage;

    private Dictionary<string, WikiPage> _byId;

    private void RebuildIndexIfNeeded()
    {
        if (_byId != null) return;
        _byId = new Dictionary<string, WikiPage>();
        foreach (var p in allPages)
        {
            if (p == null || string.IsNullOrEmpty(p.pageId)) continue;
            _byId[p.pageId] = p;
        }
    }

    public WikiPage GetById(string id)
    {
        RebuildIndexIfNeeded();
        return !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var p) ? p : null;
    }

    private Dictionary<string, WikiPage> _byTitleLower;

    /// <summary>Case-insensitive exact title match, used to resolve
    /// [[Page Title]] inline link syntax typed directly into section body
    /// text. Rebuilds its cache automatically if the page list changes size
    /// (e.g. new pages added at runtime in the editor) so stale results
    /// don't linger.</summary>
    public WikiPage GetByTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        if (_byTitleLower == null || _byTitleLower.Count != allPages.Count(p => p != null))
        {
            _byTitleLower = new Dictionary<string, WikiPage>();
            foreach (var p in allPages)
            {
                if (p == null || string.IsNullOrEmpty(p.pageTitle)) continue;
                _byTitleLower[p.pageTitle.Trim().ToLowerInvariant()] = p;
            }
        }
        return _byTitleLower.TryGetValue(title.Trim().ToLowerInvariant(), out var page) ? page : null;
    }

    public List<WikiPage> GetByCategory(WikiPage.WikiCategory cat) =>
        allPages.Where(p => p != null && p.category == cat)
                .OrderBy(p => p.pageTitle)
                .ToList();

    public List<WikiPage> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<WikiPage>();
        string q = query.Trim().ToLowerInvariant();
        return allPages.Where(p =>
        {
            if (p == null) return false;
            if (!string.IsNullOrEmpty(p.pageTitle) && p.pageTitle.ToLowerInvariant().Contains(q)) return true;
            if (!string.IsNullOrEmpty(p.blurb) && p.blurb.ToLowerInvariant().Contains(q)) return true;
            foreach (var s in p.sections)
            {
                if (!string.IsNullOrEmpty(s.heading) && s.heading.ToLowerInvariant().Contains(q)) return true;
                if (!string.IsNullOrEmpty(s.body) && s.body.ToLowerInvariant().Contains(q)) return true;
            }
            return false;
        })
        .OrderBy(p => p.pageTitle)
        .ToList();
    }
}