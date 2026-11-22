namespace MediaDownloader.App.ViewModels;

/// <summary>文件已存在时的处理方式。</summary>
public enum DuplicateChoice
{
    /// <summary>全部重新下载（覆盖同名文件）。</summary>
    DownloadAll,
    /// <summary>跳过已存在的文件，只下载其余的。</summary>
    Skip,
    /// <summary>什么都不做。</summary>
    Cancel
}
