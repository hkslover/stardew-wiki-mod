using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewWikiAgent.Wiki;

internal sealed class MediaWikiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // StripHtml runs on large wiki HTML per read; compile the patterns once.
    private static readonly Regex TagRegex = new(
        "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>|<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient http = new();
    private readonly ResponseCache cache = new(capacity: 64);
    private readonly string apiUrl;

    public MediaWikiClient(string apiUrl, TimeSpan timeout)
    {
        this.apiUrl = apiUrl;
        this.http.Timeout = timeout;
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("StardewWikiAgent/0.1 (SMAPI mod)");
    }

    public async Task<string> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { error = "query 不能为空" }, JsonOptions);

        JsonDocument data = await this.GetAsync(new Dictionary<string, string>
        {
            ["action"] = "query",
            ["list"] = "search",
            ["srsearch"] = query.Trim(),
            ["srlimit"] = Math.Clamp(limit, 1, 8).ToString(),
            ["srnamespace"] = "0",
            ["srprop"] = "snippet|sectiontitle",
            ["redirects"] = "1"
        }, cancellationToken);
        using (data)
        {
            if (!data.RootElement.TryGetProperty("query", out JsonElement queryElement)
                || !queryElement.TryGetProperty("search", out JsonElement results))
                return JsonSerializer.Serialize(new { error = "Wiki 没有返回搜索结果" }, JsonOptions);

            var output = new List<object>();
            foreach (JsonElement result in results.EnumerateArray())
            {
                string title = GetString(result, "title");
                output.Add(new
                {
                    title,
                    section = StripHtml(GetString(result, "sectiontitle")),
                    snippet = StripHtml(GetString(result, "snippet")),
                    source = PageUrl(title)
                });
            }
            return JsonSerializer.Serialize(new { query, results = output }, JsonOptions);
        }
    }

    public async Task<string> ReadAsync(string page, string? section, string? focus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(page))
            return JsonSerializer.Serialize(new { error = "page 不能为空" }, JsonOptions);

        string? sectionIndex = null;
        if (!string.IsNullOrWhiteSpace(section) && section != "0" && !int.TryParse(section, out _))
            sectionIndex = await ResolveSectionAsync(page.Trim(), section.Trim(), cancellationToken);
        else if (!string.IsNullOrWhiteSpace(section))
            sectionIndex = section.Trim();

        if (sectionIndex is null)
        {
            JsonDocument sections = await this.GetAsync(new Dictionary<string, string>
            {
                ["action"] = "parse", ["page"] = page.Trim(), ["prop"] = "sections", ["redirects"] = "1"
            }, cancellationToken);
            using (sections)
            {
                if (!sections.RootElement.TryGetProperty("parse", out JsonElement parse)
                    || !parse.TryGetProperty("sections", out JsonElement sectionList))
                    return ErrorForPage(page, sections.RootElement);
                var list = sectionList.EnumerateArray().Select(item => new
                {
                    index = GetString(item, "index"),
                    title = GetString(item, "line")
                }).ToArray();
                return JsonSerializer.Serialize(new
                {
                    title = page,
                    sections = list,
                    hint = "请根据相关章节标题再次调用 wiki_read；section=0 可读取简介。",
                    source = PageUrl(page)
                }, JsonOptions);
            }
        }

        JsonDocument body = await this.GetAsync(new Dictionary<string, string>
        {
            ["action"] = "parse",
            ["page"] = page.Trim(),
            ["section"] = sectionIndex,
            ["prop"] = "text",
            ["redirects"] = "1"
        }, cancellationToken);
        using (body)
        {
            if (!body.RootElement.TryGetProperty("parse", out JsonElement parse)
                || !parse.TryGetProperty("text", out JsonElement html))
                return ErrorForPage(page, body.RootElement);
            string text = LimitText(StripHtml(GetElementText(html)), focus);
            return JsonSerializer.Serialize(new
            {
                title = GetString(parse, "title", page),
                section = sectionIndex,
                content = text,
                source = PageUrl(page)
            }, JsonOptions);
        }
    }

    private async Task<string?> ResolveSectionAsync(string page, string wanted, CancellationToken cancellationToken)
    {
        JsonDocument data = await this.GetAsync(new Dictionary<string, string>
        {
            ["action"] = "parse", ["page"] = page, ["prop"] = "sections", ["redirects"] = "1"
        }, cancellationToken);
        using (data)
        {
            if (!data.RootElement.TryGetProperty("parse", out JsonElement parse)
                || !parse.TryGetProperty("sections", out JsonElement sections))
                return null;
            string normalized = Normalize(wanted);
            foreach (JsonElement item in sections.EnumerateArray())
            {
                string title = GetString(item, "line");
                if (Normalize(title) == normalized || Normalize(title).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    return GetString(item, "index");
            }
        }
        return null;
    }

    private async Task<JsonDocument> GetAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        string query = string.Join("&", parameters.Select(pair =>
            $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
        string requestUri = this.apiUrl + "?format=json&formatversion=2&" + query;

        // Wiki content is effectively static for a session, so cache successful raw
        // bodies. Only the string is cached; each caller parses its own disposable
        // JsonDocument. This also collapses the duplicate section-list fetch (a
        // named section that fails to resolve then falls back to the TOC branch)
        // into a single network round-trip.
        if (this.cache.TryGet(requestUri, out string cachedBody))
            return JsonDocument.Parse(cachedBody);

        using HttpResponseMessage response = await this.http.GetAsync(requestUri, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        this.cache.Set(requestUri, body);
        return JsonDocument.Parse(body);
    }

    private static string ErrorForPage(string page, JsonElement root)
    {
        string message = root.TryGetProperty("error", out JsonElement error)
            ? GetString(error, "info", GetString(error, "message", "页面不存在或 Wiki 返回错误"))
            : "页面不存在或 Wiki 没有返回正文";
        return JsonSerializer.Serialize(new { error = $"无法读取页面「{page}」：{message}" }, JsonOptions);
    }

    private static string LimitText(string text, string? focus)
    {
        const int max = 7000;
        if (text.Length <= max)
            return text;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            string[] blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            var selected = blocks.Where(block => block.Contains(focus, StringComparison.OrdinalIgnoreCase)).ToList();
            if (selected.Count > 0)
            {
                string focused = string.Join("\n\n", selected);
                return focused[..Math.Min(max, focused.Length)] + "\n[按问题关键词截取]";
            }
        }
        return text[..max] + "\n[正文过长，已截取]";
    }

    private static string StripHtml(string html)
    {
        string text = TagRegex.Replace(html ?? "", " ");
        text = WebUtility.HtmlDecode(text);
        return WhitespaceRegex.Replace(text, " ").Trim();
    }

    private static string GetElementText(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
    }

    private static string GetString(JsonElement element, string property, string fallback = "")
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static string Normalize(string value) => WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), "");

    private static string PageUrl(string title) =>
        "https://zh.stardewvalleywiki.com/" + Uri.EscapeDataString(title).Replace("%2F", "/");

    /// <summary>Small thread-safe LRU cache mapping a request URI to its raw response body.</summary>
    private sealed class ResponseCache
    {
        private readonly int capacity;
        private readonly object gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> map;
        private readonly LinkedList<Entry> order = new();

        public ResponseCache(int capacity)
        {
            this.capacity = Math.Max(1, capacity);
            this.map = new Dictionary<string, LinkedListNode<Entry>>(this.capacity, StringComparer.Ordinal);
        }

        public bool TryGet(string key, out string value)
        {
            lock (this.gate)
            {
                if (this.map.TryGetValue(key, out LinkedListNode<Entry>? node))
                {
                    this.order.Remove(node);
                    this.order.AddFirst(node);
                    value = node.Value.Body;
                    return true;
                }
            }
            value = "";
            return false;
        }

        public void Set(string key, string body)
        {
            lock (this.gate)
            {
                if (this.map.TryGetValue(key, out LinkedListNode<Entry>? existing))
                {
                    existing.Value = new Entry(key, body);
                    this.order.Remove(existing);
                    this.order.AddFirst(existing);
                    return;
                }

                var node = new LinkedListNode<Entry>(new Entry(key, body));
                this.map[key] = node;
                this.order.AddFirst(node);
                if (this.map.Count > this.capacity && this.order.Last is { } lru)
                {
                    this.order.RemoveLast();
                    this.map.Remove(lru.Value.Key);
                }
            }
        }

        private readonly record struct Entry(string Key, string Body);
    }
}
