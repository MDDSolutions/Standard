using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public class ManifestStoreHttp
    {
        private readonly HttpClient _client = new HttpClient();
        private readonly Uri _manifestUri;

        public ManifestStoreHttp(string manifestUrlWithSas)
        {
            _manifestUri = new Uri(manifestUrlWithSas);
        }

        public async Task<BackupManifest> TryLoadAsync()
        {
            try
            {
                using (var response = await _client.GetAsync(_manifestUri).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        return BackupManifest.LoadFromStream(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task SaveAsync(BackupManifest manifest)
        {
            // Write to memory first (atomic replacement pattern)
            var bytes = manifest.ToBytes();
            using (var content = new ByteArrayContent(bytes))
            {
                content.Headers.Add("x-ms-blob-type", "BlockBlob");
                var response = await _client.PutAsync(_manifestUri, content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
        }

        public async Task DeleteAsync()
        {
            try
            {
                var response = await _client.DeleteAsync(_manifestUri).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch { /* ignore */ }
        }
    }
}
