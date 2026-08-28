using System.IO;

namespace Petapeta.Services;

/// <summary>ステージングのファイル名確保(#8)。UI 非依存(#23)。</summary>
internal static class FileNaming
{
    /// <summary>
    /// ディレクトリ内で重複しないファイルパスをアトミックに確保する。
    /// 存在チェック方式は並走時に同じパスを返し得るため、CreateNew で
    /// 実際に確保できるまで連番(" (2)" …)を進める(#8)。
    /// </summary>
    internal static string CreateUniquePath(string dir, string baseName, string extension)
    {
        for (var i = 1; ; i++)
        {
            var path = Path.Combine(dir, i == 1 ? baseName + extension : $"{baseName} ({i}){extension}");
            try
            {
                using (new FileStream(path, FileMode.CreateNew)) { }
                return path;
            }
            catch (IOException)
            {
                // 既に存在 → 次の連番へ
            }
        }
    }
}
