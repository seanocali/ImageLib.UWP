﻿
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;
using ImageLib.Helpers;
using System.Threading;
using Windows.Storage.Streams;
using ImageLib.Cache;
using ImageLib.Schedulers;
using Windows.System.Threading;

namespace ImageLib
{
    public class ImageLoader
    {
        private static readonly object LockObject = new object();
        private TaskScheduler _sequentialScheduler;
        private static ImageLoader _instance;
        public static ImageLoader Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (LockObject)
                    {
                        if (_instance == null) _instance = new ImageLoader();
                    }
                }
                return _instance;
            }
        }
        protected ImageLoader()
        {
            _sequentialScheduler = new LimitedConcurrencyLevelTaskScheduler(1, WorkItemPriority.Normal, true);
        }

        protected virtual void CheckConfig()
        {
            if (ImageConfig.Default == null)
            {
                throw new InvalidOperationException("ImageLoader configuration was not setted, please Initialize ImageLoader instance with JetImageLoaderConfiguration");
            }
        }

        /// <summary>
        /// Async loading image from cache or network
        /// </summary>
        /// <param name="imageUrl">Url of the image to load</param>
        /// <returns>BitmapImage if load was successfull or null otherwise</returns>
        public virtual async Task<BitmapImage> LoadImage(string imageUrl, CancellationTokenSource cancellationTokenSource)
        {
            return await LoadImage(new Uri(imageUrl), cancellationTokenSource);
        }

        /// <summary>
        /// Async loading image from cache or network
        /// </summary>
        /// <param name="imageUri">Uri of the image to load</param>
        /// <returns>BitmapImage if load was successfull or null otherwise</returns>
        public virtual async Task<BitmapImage> LoadImage(Uri imageUri, CancellationTokenSource cancellationTokenSource)
        {
            CheckConfig();
            var bitmapImage = new BitmapImage();
            await bitmapImage.SetSourceAsync(await LoadImageStream(imageUri, cancellationTokenSource));
            return bitmapImage;
        }

        /// <summary>
        /// Async loading image stream from cache or network
        /// </summary>
        /// <param name="imageUri">Uri of the image to load</param>
        /// <returns>Stream of the image if load was successfull, null otherwise</returns>
        public virtual async Task<IRandomAccessStream> LoadImageStream(Uri imageUri,
            CancellationTokenSource cancellationTokenSource)
        {
            CheckConfig();
            if (imageUri == null)
            {
                return null;
            }
            var imageUrl = imageUri.AbsoluteUri;
            //有Cache情况，先加载Cache
            if (ImageConfig.Default.CacheMode != CacheMode.NoCache)
            {
                //加载Cache
                var resultFromCache = await LoadImageStreamFromCache(imageUri);
                if (resultFromCache != null)
                {
                    return resultFromCache;
                }
            }
            try
            {
                ImageLog.Log("[network] loading " + imageUrl);
                IRandomAccessStream randStream = null;
                //如果自定义UriParser,使用自定义Parser,反之使用默认方式.
                if (ImageConfig.Default.UriParser != null)
                {
                    randStream = await ImageConfig.Default.
                        UriParser.GetStreamFromUri(imageUri, cancellationTokenSource.Token);
                }
                else
                {
                    randStream = await imageUri.GetStreamFromUri(cancellationTokenSource.Token);
                }
                if (randStream == null)
                {
                    ImageLog.Log("[error] failed to download: " + imageUrl);
                    return null;
                }
                var inMemoryStream = new InMemoryRandomAccessStream();
                using (randStream)
                {
                    var copyAction = RandomAccessStream.CopyAndCloseAsync(
                              randStream.GetInputStreamAt(0L),
                              inMemoryStream.GetOutputStreamAt(0L));
                    await copyAction.AsTask(cancellationTokenSource.Token);
                }
                randStream = inMemoryStream;

                ImageLog.Log("[network] loaded " + imageUrl);

                if (ImageConfig.Default.CacheMode != CacheMode.NoCache)
                {
                    if (ImageConfig.Default.CacheMode == CacheMode.MemoryAndStorageCache ||
                        ImageConfig.Default.CacheMode == CacheMode.OnlyMemoryCache)
                    {
                        if (randStream != null)
                        {
                            ImageConfig.Default.MemoryCacheImpl.Put(imageUrl, randStream);
                        }
                    }

                    if (ImageConfig.Default.CacheMode == CacheMode.MemoryAndStorageCache ||
                    ImageConfig.Default.CacheMode == CacheMode.OnlyStorageCache)
                    {
                        //是http or https 才加入本地缓存
                        if (imageUri.IsWebScheme())
                        {
                            await Task.Factory.StartNew(() =>
                              {
                                  ImageLog.Log(string.Format("{0} in task t-{1}", imageUri, Task.CurrentId));
                                  // Async saving to the storage cache without await
                                  var saveAsync = ImageConfig.Default.StorageCacheImpl.SaveAsync(imageUrl, randStream)
                                        .ContinueWith(task =>
                                            {
                                                ImageLog.Log(string.Format("{0} in task t1-{1}", imageUri, Task.CurrentId));

                                                if (task.IsFaulted || !task.Result)
                                                {
                                                    ImageLog.Log("[error] failed to save in storage: " + imageUri);
                                                }
                                            }
                                    );
                              }, default(CancellationToken), TaskCreationOptions.AttachedToParent, this._sequentialScheduler);
                        }
                    }
                }

                return randStream;
            }
            catch
            {
                ImageLog.Log("[error] failed to save loaded image: " + imageUrl);
            }

            // May be another thread has saved image to the cache
            // It is real working case
            if (ImageConfig.Default.CacheMode != CacheMode.NoCache)
            {
                var resultFromCache = await LoadImageStreamFromCache(imageUri);
                if (resultFromCache != null)
                {
                    return resultFromCache;
                }
            }

            ImageLog.Log("[error] failed to load image stream from cache and network: " + imageUrl);
            return null;
        }

        /// <summary>
        /// Loads image stream from memory or storage cachecache
        /// </summary>
        /// <param name="imageUrl"></param>
        /// <returns>Steam of the image or null if it was not found in cache</returns>
        protected virtual async Task<IRandomAccessStream> LoadImageStreamFromCache(Uri imageUri)
        {
            var imageUrl = imageUri.AbsoluteUri;
            if (ImageConfig.Default.CacheMode == CacheMode.MemoryAndStorageCache ||
                ImageConfig.Default.CacheMode == CacheMode.OnlyMemoryCache)
            {
                IRandomAccessStream memoryStream;
                if (ImageConfig.Default.MemoryCacheImpl.TryGetValue(imageUrl, out memoryStream))
                {
                    ImageLog.Log("[memory] " + imageUrl);
                    return memoryStream;
                }
            }

            if (ImageConfig.Default.CacheMode == CacheMode.MemoryAndStorageCache ||
                ImageConfig.Default.CacheMode == CacheMode.OnlyStorageCache)
            {
                //网络uri且缓存可用
                if (imageUri.IsWebScheme() && await ImageConfig.Default.StorageCacheImpl.IsCacheExistsAndAlive(imageUrl))
                {
                    ImageLog.Log("[storage] " + imageUrl);
                    var storageStream = await ImageConfig.Default.StorageCacheImpl.LoadCacheStreamAsync(imageUrl);
                    // Moving cache to the memory
                    if (ImageConfig.Default.CacheMode == CacheMode.MemoryAndStorageCache
                        && storageStream != null)
                    {
                        ImageConfig.Default.MemoryCacheImpl.Put(imageUrl, storageStream);
                    }
                    return storageStream;
                }
            }
            return null;
        }


    }
}
