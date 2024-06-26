﻿using WikiClientLibrary.Pages;

namespace WikiClientLibrary.Generators;

/// <summary>
/// Extension method for constructing generators from <see cref="WikiPage"/>.
/// </summary>
public static class WikiPageExtensions
{

    /// <summary>
    /// Creates a <see cref="LinksGenerator"/> instance from the specified page,
    /// which generates pages from all links on the page.
    /// </summary>
    /// <param name="page">The page.</param>
    public static LinksGenerator CreateLinksGenerator(this WikiPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        return new LinksGenerator(page.Site, page.PageStub);
    }

    /// <summary>
    /// Creates a <see cref="FilesGenerator"/> instance from the specified page,
    /// which generates files from all used files on the page.
    /// </summary>
    /// <param name="page">The page.</param>
    public static FilesGenerator CreateFilesGenerator(this WikiPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        return new FilesGenerator(page.Site, page.PageStub);
    }

    /// <summary>
    /// Creates a <see cref="TransclusionsGenerator"/> instance from the specified page,
    /// which generates pages from all pages (typically templates) transcluded in the page.
    /// </summary>
    /// <param name="page">The page.</param>
    public static TransclusionsGenerator CreateTransclusionsGenerator(this WikiPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        return new TransclusionsGenerator(page.Site, page.PageStub);
    }

    /// <summary>
    /// Creates a <see cref="RevisionsGenerator"/> instance from the specified page,
    /// which enumerates the sequence of revisions on the page.
    /// </summary>
    /// <param name="page">The page.</param>
    public static RevisionsGenerator CreateRevisionsGenerator(this WikiPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        return new RevisionsGenerator(page.Site, page.PageStub);
    }

    /// <summary>
    /// Creates a <see cref="CategoriesGenerator"/> instance from the specified page,
    /// which enumerates the categories used on the page.
    /// </summary>
    /// <param name="page">The page.</param>
    public static CategoriesGenerator CreateCategoriesGenerator(this WikiPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        return new CategoriesGenerator(page.Site, page.PageStub);
    }

}
