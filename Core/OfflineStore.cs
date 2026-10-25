using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace legado_wp.Core
{
    /// <summary>
    /// 离线全文缓存：LocalFolder/Offline/&lt;md5(bookUrl)&gt;/ 下每章一个文件 + meta.json。
    /// </summary>
    public static class OfflineStore
    {
        public class OfflineMeta
        {
            public string BookName;
            public string Author;
            public string BookUrl;
            public string Intro;
            public string LastChapter;
            public string TocUrl;
            public int ChapterCount;
            public long SavedAt;
            public BookSource Source;      // 离线后书源被删也能继续读
            public List<BookChapter> Chapters; // 离线目录（无网络时目录页可用）
        }

        private static async Task<StorageFolder> RootAsync()
        {
            return await ApplicationData.Current.LocalFolder
                .CreateFolderAsync("Offline", CreationCollisionOption.OpenIfExists);
        }

        public static string KeyOf(string bookUrl)
        {
            try
            {
                var alg = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
                var buf = CryptographicBuffer.ConvertStringToBinary(bookUrl ?? "", BinaryStringEncoding.Utf8);
                return CryptographicBuffer.EncodeToHexString(alg.HashData(buf));
            }
            catch { return "book"; }
        }

        private static async Task<StorageFolder> BookFolderAsync(string bookUrl)
        {
            var root = await RootAsync();
            return await root.CreateFolderAsync(KeyOf(bookUrl), CreationCollisionOption.OpenIfExists);
        }

        public static async Task SaveChapterAsync(string bookUrl, int index, string content)
        {
            try
            {
                var folder = await BookFolderAsync(bookUrl);
                var file = await folder.CreateFileAsync(index + ".txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, content ?? "");
            }
            catch { /* 单章写失败不断流程 */ }
        }

        public static async Task<string> LoadChapterAsync(string bookUrl, int index)
        {
            try
            {
                var folder = await BookFolderAsync(bookUrl);
                var file = await folder.TryGetItemAsync(index + ".txt") as StorageFile;
                if (file == null) return null;
                return await FileIO.ReadTextAsync(file);
            }
            catch { return null; }
        }

        public static async Task SaveMetaAsync(OfflineMeta meta)
        {
            try
            {
                var folder = await BookFolderAsync(meta.BookUrl);
                var file = await folder.CreateFileAsync("meta.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(meta));
            }
            catch { }
        }

        public static async Task<OfflineMeta> LoadMetaAsync(string bookUrl)
        {
            try
            {
                var folder = await BookFolderAsync(bookUrl);
                var file = await folder.TryGetItemAsync("meta.json") as StorageFile;
                if (file == null) return null;
                var json = await FileIO.ReadTextAsync(file);
                return JsonConvert.DeserializeObject<OfflineMeta>(json);
            }
            catch { return null; }
        }

        public static async Task<bool> HasOfflineAsync(string bookUrl)
        {
            var meta = await LoadMetaAsync(bookUrl);
            return meta != null && meta.ChapterCount > 0;
        }
    }
}
