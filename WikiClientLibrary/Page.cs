﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WikiClientLibrary.Client;
using WikiClientLibrary.Generators;

namespace WikiClientLibrary
{
    /// <summary>
    /// Represents a page on MediaWiki site.
    /// </summary>
    public class Page
    {
        public Page(Site site, string title) : this(site, title, BuiltInNamespaces.Main)
        {
        }

        public Page(Site site, string title, int defaultNamespaceId)
        {
            if (site == null) throw new ArgumentNullException(nameof(site));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentNullException(nameof(title));
            Site = site;
            WikiClient = Site.WikiClient;
            Debug.Assert(WikiClient != null);
            Title = WikiLink.NormalizeWikiLink(site, title, defaultNamespaceId);
        }

        internal Page(Site site)
        {
            if (site == null) throw new ArgumentNullException(nameof(site));
            Site = site;
            WikiClient = Site.WikiClient;
            Debug.Assert(WikiClient != null);
            Site = site;
        }

        /// <summary>
        /// Synonym for <c>Site.WikiClient</c> .
        /// </summary>
        public WikiClient WikiClient { get; }

        public Site Site { get; }

        public int Id { get; private set; }

        public int NamespaceId { get; private set; }

        /// <summary>
        /// Gets the id of last revision. In some cases, this property
        /// has non-zero value while <see cref="LastRevision"/> is <c>null</c>.
        /// See <see cref="UpdateContentAsync(string)"/> for more information.
        /// </summary>
        public int LastRevisionId { get; private set; }

        /// <summary>
        /// Content length, in bytes.
        /// </summary>
        /// <remarks>
        /// Even if you haven't fetched content of the page when calling <see cref="RefreshAsync()"/>,
        /// this property will still get its value.
        /// </remarks>
        public int ContentLength { get; private set; }

        /// <summary>
        /// Page touched timestamp. It can be later than the timestamp of the last revision.
        /// </summary>
        /// <remarks>See https://www.mediawiki.org/wiki/Manual:Page_table#page_touched .</remarks>
        public DateTime LastTouched { get; private set; }

        public IReadOnlyCollection<ProtectionInfo> Protections { get; private set; }

        /// <summary>
        /// Applicable protection types. (MediaWiki 1.25)
        /// </summary>
        public IReadOnlyCollection<string> RestrictionTypes { get; private set; }

        /// <summary>
        /// Gets whether the page exists.
        /// For category, gets whether the categories description page exists.
        /// </summary>
        public bool Exists { get; private set; }

        /// <summary>
        /// Content model. (MediaWiki 1.22)
        /// </summary>
        public string ContentModel { get; private set; }

        /// <summary>
        /// Page language. (MediaWiki 1.24)
        /// </summary>
        /// <remarks>See https://www.mediawiki.org/wiki/API:PageLanguage .</remarks>
        public string PageLanguage { get; private set; }

        /// <summary>
        /// Gets the normalized title of the page.
        /// </summary>
        /// <remarks>
        /// Normalized title is a title with underscores(_) replaced by spaces,
        /// and the first letter is usually upper-case.
        /// </remarks>
        public string Title { get; private set; }

        /// <summary>
        /// Gets / Sets the content of the page.
        /// </summary>
        /// <remarks>You should have invoked <c>RefreshAsync(PageQueryOptions.FetchContent)</c> before trying to read the content of the page.</remarks>
        public string Content { get; set; }

        /// <summary>
        /// Gets the latest revision of the page.
        /// </summary>
        /// <remarks>Make sure to invoke <see cref="RefreshAsync()"/> before getting the value.</remarks>
        public Revision LastRevision { get; private set; }

        /// <summary>
        /// Gets / Sets the options when querying page information.
        /// </summary>
        public PageQueryOptions QueryOptions { get; private set; }

        ///// <summary>
        ///// Determines whether the page is a disambiguation page.
        ///// </summary>
        //public bool IsDisambiguation { get; private set; }

        private static bool AreIdEquals(int id1, int id2)
        {
            if (id1 == id2) return false;
            // For inexistent pages, id is negative.
            if (id2 > 0 && id1 > 0 || Math.Sign(id2) != Math.Sign(id1)) return true;
            return false;
        }

        #region Query

        private static readonly Page[] EmptyPages = new Page[0];

        /// <summary>
        /// Loads page information from JSON.
        /// </summary>
        /// <param name="prop">query.pages.xxx property.</param>
        /// <param name="options">Provides options when performing the query.</param>
        internal void LoadFromJson(JProperty prop, PageQueryOptions options)
        {
            var id = Convert.ToInt32(prop.Name);
            // I'm not sure whether this assertion holds.
            Debug.Assert(id != 0);
            // The page has been overwritten, or deleted.
            if (Id != 0 && !AreIdEquals(Id, id))
                WikiClient.Logger?.Warn($"Detected change of page id: {Title}, {Id}");
            Id = id;
            var page = (JObject) prop.Value;
            OnLoadPageInfo(page);
            // TODO Cache content
            LoadLastRevision(page);
            QueryOptions = options;
        }

        protected virtual void OnLoadPageInfo(JObject jpage)
        {
            Title = (string)jpage["title"];
            // Invalid page title (like Special:)
            if (jpage["invalid"] != null)
            {
                var reason = (string)jpage["invalidreason"];
                throw new OperationFailedException(reason);
            }
            NamespaceId = (int)jpage["ns"];
            Exists = jpage["missing"] == null;
            ContentModel = (string)jpage["contentmodel"];
            PageLanguage = (string)jpage["pagelanguage"];
            if (Exists)
            {
                ContentLength = (int)jpage["length"];
                LastRevisionId = (int)jpage["lastrevid"];
                LastTouched = (DateTime)jpage["touched"];
                Protections = ((JArray)jpage["protection"]).ToObject<IReadOnlyCollection<ProtectionInfo>>(
                    Utility.WikiJsonSerializer);
                RestrictionTypes = ((JArray)jpage["restrictiontypes"])?.ToObject<IReadOnlyCollection<string>>(
                    Utility.WikiJsonSerializer);
            }
            else
            {
                ContentLength = 0;
                LastRevisionId = 0;
                LastTouched = DateTime.MinValue;
                Protections = null;
                RestrictionTypes = null;
            }
        }

        /// <summary>
        /// Loads the first revision from JSON, assuming it's the latest revision.
        /// </summary>
        /// <param name="pageInfo">query.pages.xxx property value.</param>
        private void LoadLastRevision(JObject pageInfo)
        {
            var revision = (JObject) pageInfo["revisions"]?.FirstOrDefault();
            if (revision != null)
            {
                LastRevision = revision.ToObject<Revision>(Utility.WikiJsonSerializer);
                LastRevisionId = LastRevision.Id;
                Content = LastRevision.Content;
            }
            else
            {
                // No revisions available.
                LastRevision = null;
                LastRevisionId = 0;
            }
        }

        internal static T CreateInstance<T>(Site site) where T : Page
        {
            var t = typeof (T);
            if (t == typeof (Page)) return new Page(site) as T;
            if (t == typeof (Category)) return new Category(site) as T;
            throw new NotSupportedException($"Can not create instance of {t} .");
        }

        /// <summary>
        /// Creates a list of <see cref="Page"/> based on JSON query result.
        /// </summary>
        /// <param name="site">A <see cref="Site"/> object.</param>
        /// <param name="queryNode">The <c>qurey</c> node value object of JSON result.</param>
        /// <param name="options">Provides options when performing the query.</param>
        /// <returns>Retrived pages.</returns>
        internal static IList<T> FromJsonQueryResult<T>(Site site, JObject queryNode, PageQueryOptions options)
            where T : Page
        {
            if (site == null) throw new ArgumentNullException(nameof(site));
            if (queryNode == null) throw new ArgumentNullException(nameof(queryNode));
            var pages = (JObject) queryNode["pages"];
            if (pages == null) return new T[0];
            site.Logger?.Trace($"Fetching {pages.Count} pages. {options}");
            return pages.Properties().Select(page =>
            {
                var newInst = CreateInstance<T>(site);
                newInst.LoadFromJson(page, options);
                return newInst;
            }).ToList();
        }

        /// <summary>
        /// Fetch information for the page.
        /// This overload will not fetch content.
        /// </summary>
        /// <remarks>
        /// For fetching multiple pages at one time, see <see cref="PageExtensions.RefreshAsync(IEnumerable{Page})"/>.
        /// </remarks>
        public Task RefreshAsync()
        {
            return RefreshAsync(PageQueryOptions.None);
        }

        /// <summary>
        /// Fetch information for the page.
        /// </summary>
        /// <param name="options">Options when querying for the pages.</param>
        /// <remarks>
        /// For fetching multiple pages at one time, see <see cref="PageExtensions.RefreshAsync(IEnumerable{Page}, PageQueryOptions)"/>.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Circular redirect detected when resolving redirects.</exception>
        public Task RefreshAsync(PageQueryOptions options)
        {
            return RequestManager.RefreshPagesAsync(new[] {this}, options);
        }

        /// <summary>
        /// Enumerate revisions of the page.
        /// </summary>
        /// <param name="options">Options for revision listing.</param>
        public IAsyncEnumerable<Revision> EnumRevisionsAsync(RevisionsQueryOptions options)
        {
            return RequestManager.EnumRevisionsAsync(Site, Title, options);
        }

        /// <summary>
        /// Enumerate revisions of the page, descending in time, without revision content.
        /// </summary>
        public IAsyncEnumerable<Revision> EnumRevisionsAsync()
        {
            return EnumRevisionsAsync(RevisionsQueryOptions.None);
        }

        #endregion

        #region Modification

        /// <summary>
        /// Submits content contained in <see cref="Content"/>, making edit to the page.
        /// (MediaWiki 1.16)
        /// </summary>
        /// <remarks>
        /// This action will refill <see cref="Id" />, <see cref="Title"/>,
        /// <see cref="ContentModel"/>, <see cref="LastRevisionId"/>, and invalidates
        /// <see cref="ContentLength"/>, <see cref="LastRevision"/>, and <see cref="LastTouched"/>.
        /// You should call <see cref="RefreshAsync()"/> again
        /// if you're interested in them.
        /// </remarks>
        public Task UpdateContentAsync(string summary)
        {
            return UpdateContentAsync(summary, false, true, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Submits content contained in <see cref="Content"/>, making edit to the page.
        /// (MediaWiki 1.16)
        /// </summary>
        /// <remarks>
        /// This action will refill <see cref="Id" />, <see cref="Title"/>,
        /// <see cref="ContentModel"/>, <see cref="LastRevisionId"/>, and invalidates
        /// <see cref="ContentLength"/>, <see cref="LastRevision"/>, and <see cref="LastTouched"/>.
        /// You should call <see cref="RefreshInfoAsync"/> or <see cref="RefreshContentAsync"/> again
        /// if you're interested in them.
        /// </remarks>
        public Task UpdateContentAsync(string summary, bool minor)
        {
            return UpdateContentAsync(summary, minor, true, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Submits content contained in <see cref="Content"/>, making edit to the page.
        /// (MediaWiki 1.16)
        /// </summary>
        /// <remarks>
        /// This action will refill <see cref="Id" />, <see cref="Title"/>,
        /// <see cref="ContentModel"/>, <see cref="LastRevisionId"/>, and invalidates
        /// <see cref="ContentLength"/>, <see cref="LastRevision"/>, and <see cref="LastTouched"/>.
        /// You should call <see cref="RefreshInfoAsync"/> or <see cref="RefreshContentAsync"/> again
        /// if you're interested in them.
        /// </remarks>
        public Task UpdateContentAsync(string summary, bool minor, bool bot)
        {
            return UpdateContentAsync(summary, minor, bot, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Submits content contained in <see cref="Content"/>, making edit to the page.
        /// (MediaWiki 1.16)
        /// </summary>
        /// <returns><c>true</c> if page content has been changed; <c>false</c> otherwise.</returns>
        /// <remarks>
        /// This action will refill <see cref="Id" />, <see cref="Title"/>,
        /// <see cref="ContentModel"/>, <see cref="LastRevisionId"/>, and invalidates
        /// <see cref="ContentLength"/>, <see cref="LastRevision"/>, and <see cref="LastTouched"/>.
        /// You should call <see cref="RefreshInfoAsync"/> or <see cref="RefreshContentAsync"/> again
        /// if you're interested in them.
        /// </remarks>
        /// <exception cref="OperationConflictException">Edit conflict detected.</exception>
        /// <exception cref="UnauthorizedOperationException">You have no rights to edit the page.</exception>
        public async Task<bool> UpdateContentAsync(string summary, bool minor, bool bot, AutoWatchBehavior watch)
        {
            var tokenTask = Site.GetTokenAsync("edit");
            await WikiClient.WaitForThrottleAsync();
            var token = await tokenTask;
            // When passing this to the Edit API, always pass the token parameter last
            // (or at least after the text parameter). That way, if the edit gets interrupted,
            // the token won't be passed and the edit will fail.
            // This is done automatically by mw.Api.
            JToken jresult;
            try
            {
                jresult = await WikiClient.GetJsonAsync(new
                {
                    action = "edit",
                    title = Title,
                    minor = minor,
                    bot = bot,
                    recreate = true,
                    maxlag = 5,
                    basetimestamp = LastRevision?.TimeStamp,
                    watchlist = watch,
                    summary = summary,
                    text = Content,
                    token = token,
                });
            }
            catch (OperationFailedException ex)
            {
                switch (ex.ErrorCode)
                {
                    case "protectedpage":
                        throw new UnauthorizedOperationException(ex);
                    default:
                        throw;
                }
            }
            var jedit = jresult["edit"];
            var result = (string) jedit["result"];
            if (result == "Success")
            {
                if (jedit["nochange"] != null) return false;
                ContentModel = (string) jedit["contentmodel"];
                LastRevisionId = (int) jedit["newrevid"];
                Id = (int) jedit["pageid"];
                Title = (string) jedit["title"];
                return true;
            }
            throw new OperationFailedException(result, (string) null);
        }

        #endregion

        #region Management

        /// <summary>
        /// Get token and wait for a while.
        /// </summary>
        private async Task<string> GetTokenAndWaitAsync(string tokenType)
        {
            var tokenTask = Site.GetTokenAsync("csrf");
            await WikiClient.WaitForThrottleAsync();
            return await tokenTask;
        }

        /// <summary>
        /// Moves (renames) a page. (MediaWiki 1.12)
        /// </summary>
        public Task MoveAsync(string newTitle, string reason, PageMovingOptions options)
        {
            return MoveAsync(newTitle, reason, options, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Moves (renames) a page. (MediaWiki 1.12)
        /// </summary>
        public Task MoveAsync(string newTitle, string reason)
        {
            return MoveAsync(newTitle, reason, PageMovingOptions.None, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Moves (renames) a page. (MediaWiki 1.12)
        /// </summary>
        public Task MoveAsync(string newTitle)
        {
            return MoveAsync(newTitle, null, PageMovingOptions.None, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Moves (renames) a page. (MediaWiki 1.12)
        /// </summary>
        public async Task MoveAsync(string newTitle, string reason, PageMovingOptions options, AutoWatchBehavior watch)
        {
            if (newTitle == null) throw new ArgumentNullException(nameof(newTitle));
            if (newTitle == Title) return;
            var token = await GetTokenAndWaitAsync("csrf");
            // When passing this to the Edit API, always pass the token parameter last
            // (or at least after the text parameter). That way, if the edit gets interrupted,
            // the token won't be passed and the edit will fail.
            // This is done automatically by mw.Api.
            JToken jresult;
            try
            {
                jresult = await WikiClient.GetJsonAsync(new
                {
                    action = "move",
                    from = Title,
                    to = newTitle,
                    maxlag = 5,
                    movetalk = (options & PageMovingOptions.LeaveTalk) != PageMovingOptions.LeaveTalk,
                    movesubpages = (options & PageMovingOptions.MoveSubpages) == PageMovingOptions.MoveSubpages,
                    noredirect = (options & PageMovingOptions.NoRedirect) == PageMovingOptions.NoRedirect,
                    ignorewarnings = (options & PageMovingOptions.IgnoreWarnings) == PageMovingOptions.IgnoreWarnings,
                    watchlist = watch,
                    reason = reason,
                    token = token,
                });
            }
            catch (OperationFailedException ex)
            {
                switch (ex.ErrorCode)
                {
                    case "cantmove":
                    case "protectedpage":
                    case "protectedtitle":
                        throw new UnauthorizedOperationException(ex);
                    default:
                        if (ex.ErrorCode.StartsWith("cantmove"))
                            throw new UnauthorizedOperationException(ex);
                        throw;
                }
            }
            var fromTitle = (string) jresult["move"]["from"];
            var toTitle = (string) jresult["move"]["to"];
            Site.Logger.Info($"Page {fromTitle} has been moved to {toTitle} .");
            Title = toTitle;
        }

        /// <summary>
        /// Deletes the current page.
        /// </summary>
        public Task<bool> DeleteAsync(string reason)
        {
            return DeleteAsync(reason, AutoWatchBehavior.Default);
        }

        /// <summary>
        /// Deletes the current page.
        /// </summary>
        public async Task<bool> DeleteAsync(string reason, AutoWatchBehavior watch)
        {
            var token = await GetTokenAndWaitAsync("csrf");
            JToken jresult;
            try
            {
                jresult = await WikiClient.GetJsonAsync(new
                {
                    action = "delete",
                    title = Title,
                    maxlag = 5,
                    watchlist = watch,
                    reason = reason,
                    token = token,
                });
            }
            catch (OperationFailedException ex)
            {
                switch (ex.ErrorCode)
                {
                    case "cantdelete": // Couldn't delete "title". Maybe it was deleted already by someone else
                    case "missingtitle":
                        return false;
                    case "permissiondenied":
                        throw new UnauthorizedOperationException(ex);
                }
                throw;
            }
            var title = (string) jresult["delete"]["title"];
            Exists = false;
            LastRevision = null;
            LastRevisionId = 0;
            Site.Logger.Info($"Page {title} has been deleted.");
            return true;
        }

        /// <summary>
        /// Purges the current page.
        /// </summary>
        /// <returns><c>true</c> if the page has been successfully purged.</returns>
        public async Task<bool> PurgeAsync()
        {
            JToken jresult;
            try
            {
                jresult = await WikiClient.GetJsonAsync(new
                {
                    action = "purge",
                    titles = Title,
                    forcelinkupdate = true,
                    forcerecursivelinkupdate = true,
                });
            }
            catch (OperationFailedException ex)
            {
                if (ex.ErrorCode == "cantpurge") throw new UnauthorizedOperationException(ex);
                throw;
            }
            var page = jresult["purge"].First();
            return page["purged"] != null;
        }

        #endregion

        /// <summary>
        /// 返回表示当前对象的字符串。
        /// </summary>
        /// <returns>
        /// 表示当前对象的字符串。
        /// </returns>
        public override string ToString()
        {
            return Title;
        }
    }

    /// <summary>
    /// Specifies wheter to watch the page after editing it.
    /// </summary>
    public enum AutoWatchBehavior
    {
        /// <summary>
        /// Use the preference settings. (watchlist=preferences)
        /// </summary>
        Default = 0,

        /// <summary>
        /// Do not change watchlist.
        /// </summary>
        None = 1,
        Watch = 2,
        Unwatch = 3,
    }

    /// <summary>
    /// Specifies options for moving pages.
    /// </summary>
    [Flags]
    public enum PageMovingOptions
    {
        None = 0,

        /// <summary>
        /// Do not attempt to move talk pages.
        /// </summary>
        LeaveTalk = 1,

        /// <summary>
        /// Move subpages, if applicable.
        /// </summary>
        /// <remarks>This is usually not recommended because you still cannot overwrite existing subpages.</remarks>
        MoveSubpages = 2,

        /// <summary>
        /// Don't create a redirect. Requires the suppressredirect right,
        /// which by default is granted only to bots and sysops
        /// </summary>
        NoRedirect = 4,

        /// <summary>
        /// Ignore any warnings.
        /// </summary>
        IgnoreWarnings = 8,
    }

    /// <summary>
    /// Options for refreshing a <see cref="Page"/> object.
    /// </summary>
    [Flags]
    public enum PageQueryOptions
    {
        None = 0,
        /// <summary>
        /// Fetch content of the page.
        /// </summary>
        FetchContent = 1,
        /// <summary>
        /// Resolves directs automatically. This may later change <see cref="Page.Title"/>.
        /// This option cannot be used with generators.
        /// In the case of multiple redirects, all redirects will be resolved.
        /// </summary>
        ResolveRedirects = 2,
    }

    [Flags]
    public enum RevisionsQueryOptions
    {
        None = 0,
        /// <summary>
        /// Fetch content of the revision.
        /// </summary>
        FetchContent = 1,
        /// <summary>
        /// Enumerate the oldest revision first.
        /// </summary>
        TimeAscending = 2,
    }

    /// <summary>
    /// Page protection information.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ProtectionInfo
    {
        public string Type { get; private set; }

        public string Level { get; private set; }

        public DateTime Expiry { get; private set; }

        public bool Cascade { get; private set; }

        /// <summary>
        /// 返回该实例的完全限定类型名。
        /// </summary>
        /// <returns>
        /// 包含完全限定类型名的 <see cref="T:System.String"/>。
        /// </returns>
        public override string ToString()
        {
            return $"{Type}, {Level}, {Expiry}, {(Cascade ? "Cascade" : "")}";
        }
    }

    /// <summary>
    /// Represents a revision of a page.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class Revision
    {
        [JsonProperty("revid")]
        public int Id { get; private set; }

        [JsonProperty]
        public int ParentId { get; private set; }

        /// <summary>
        /// Gets the content of the revision.
        /// </summary>
        /// <value>Wikitext source code. OR <c>null</c> if content has not been fetched.</value>
        [JsonProperty("*")]
        public string Content { get; private set; }

        [JsonProperty]
        public string Comment { get; private set; }

        [JsonProperty]
        public string ContentModel { get; private set; }

        [JsonProperty]
        public string Sha1 { get; private set; }

        [JsonProperty("user")]
        public string UserName { get; private set; }

        [JsonProperty("size")]
        public int ContentLength { get; private set; }

        [JsonProperty]
        public IList<string> Tags { get; private set; }

        /// <summary>
        /// The timestamp of revision.
        /// </summary>
        [JsonProperty]
        public DateTime TimeStamp { get; private set; }

        public RevisionFlags Flags { get; private set; }

        /// <summary>
        /// Gets a indicator of whether one or more fields has been hidden.
        /// </summary>
        /// <remarks>See https://www.mediawiki.org/wiki/Help:RevisionDelete .</remarks>
        public RevisionHiddenFields HiddenFields { get; private set; }

#pragma warning disable 649
        [JsonProperty] private bool Minor;
        [JsonProperty] private bool Bot;
        [JsonProperty] private bool New;
        [JsonProperty] private bool Anon;
        [JsonProperty] private bool UserHidden;
#pragma warning restore 649

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            Flags = RevisionFlags.None;
            if (Minor) Flags |= RevisionFlags.Minor;
            if (Bot) Flags |= RevisionFlags.Bot;
            if (New) Flags |= RevisionFlags.Create;
            if (Anon) Flags |= RevisionFlags.Annonymous;
            HiddenFields = RevisionHiddenFields.None;
            if (UserHidden) HiddenFields |= RevisionHiddenFields.User;
        }

        /// <summary>
        /// 返回该实例的完全限定类型名。
        /// </summary>
        /// <returns>
        /// 包含完全限定类型名的 <see cref="T:System.String"/>。
        /// </returns>
        public override string ToString()
        {
            var tags = Tags == null ? null : string.Join("|", Tags);
            return $"Revision#{Id}, {Flags}, {tags}, SHA1={Sha1}";
        }
    }

    /// <summary>
    /// Revision flags.
    /// </summary>
    [Flags]
    public enum RevisionFlags
    {
        None = 0,
        Minor = 1,
        /// <summary>
        /// The operation is performed by bot.
        /// This flag can only be access via <see cref="RecentChangesEntry.Flags"/>.
        /// </summary>
        Bot = 2,
        Create = 4,
        Annonymous = 8,
    }

    /// <summary>
    /// Hidden part of revision indicators.
    /// </summary>
    /// <remarks>See https://www.mediawiki.org/wiki/Help:RevisionDelete .</remarks>
    [Flags]
    public enum RevisionHiddenFields
    {
        None = 0,
        User = 1,
        //TODO Content & Comment
    }

    /// <summary>
    /// Contains additional page properties.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class PagePropertyInfo
    {
        /// <summary>
        /// Determines whether the page is a disambiguation page.
        /// This is raw value and only works when Extension:Disambiguator presents.
        /// Please use <see cref="Page.IsDisambiguation"/> instead.
        /// </summary>
        [JsonProperty]
        public bool Disambiguation { get; private set; }

        [JsonProperty]
        public string DisplayTitle { get; private set; }

        [JsonProperty("page_image")]
        public string PageImage { get; private set; }

        [JsonExtensionData]
        public IDictionary<string, object> Others { get; private set; }
    }
}
