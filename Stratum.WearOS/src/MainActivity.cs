// Copyright (C) 2022 jmh
// SPDX-License-Identifier: GPL-3.0-only

using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Gms.Common.Apis;
using Android.Gms.Wearable;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Wear.Widget;
using AndroidX.Wear.Widget.Drawer;
using Java.IO;
using Newtonsoft.Json;
using Stratum.Droid.Shared.Util;
using Stratum.Droid.Shared.Wear;
using Stratum.WearOS.Cache;
using Stratum.WearOS.Cache.View;
using Stratum.WearOS.Comparer;
using Stratum.WearOS.Fragment;
using Stratum.WearOS.Interface;
using Stratum.WearOS.Util;
using Exception = System.Exception;

namespace Stratum.WearOS
{
    [Activity(Label = "@string/displayName", MainLauncher = true, Icon = "@mipmap/ic_launcher")]
    public class MainActivity : AppCompatActivity
    {
        // Query Paths
        private const string ProtocolVersion = "protocol_v4.0";
        private const string GetSyncBundlePath = "get_sync_bundle";

        // Cache Names
        private const string AuthenticatorCacheName = "authenticators";
        private const string CategoryCacheName = "categories";

        // Lifecycle Synchronisation
        private readonly SemaphoreSlim _onCreateLock;

        // Views
        private CircularProgressLayout _circularProgressLayout;
        private LinearLayout _offlineLayout;
        private WearableNavigationDrawerView _categoryList;
        
        // Fragments
        private AuthListFragment _listFragment;

        // Data
        private AuthenticatorView _authView;
        private CategoryView _categoryView;

        private ListCache<WearAuthenticator> _authCache;
        private ListCache<WearCategory> _categoryCache;
        private CustomIconCache _customIconCache;

        private PreferenceWrapper _preferences;
        private bool _preventCategorySelectEvent;

        private CategoryListAdapter _categoryListAdapter;

        // Connection Status
        private INode _serverNode;
        private bool _isDisposed;

        public MainActivity()
        {
            _onCreateLock = new SemaphoreSlim(1, 1);
        }

        ~MainActivity()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _onCreateLock.Dispose();
                }

                _isDisposed = true;
            }

            base.Dispose(disposing);
        }

        protected override async void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            await _onCreateLock.WaitAsync();

            SetTheme(Resource.Style.AppTheme);
            SetContentView(Resource.Layout.activityMain);

            _preferences = new PreferenceWrapper(this);

            _authCache = new ListCache<WearAuthenticator>(AuthenticatorCacheName, this);
            _categoryCache = new ListCache<WearCategory>(CategoryCacheName, this);
            _customIconCache = new CustomIconCache(this);

            await Task.WhenAll(_authCache.InitAsync(), _categoryCache.InitAsync(), _customIconCache.InitAsync());

            var defaultCategory = _preferences.DefaultCategory;
            _authView = new AuthenticatorView(_authCache, defaultCategory, _preferences.SortMode);
            _categoryView = new CategoryView(_categoryCache);
            
            _listFragment = new AuthListFragment(_authView, _customIconCache);
            _listFragment.ItemClicked += OnItemClicked;

            RunOnUiThread(delegate
            {
                InitViews();

                if (!_authCache.GetItems().Any())
                {
                    ReleaseOnCreateLock();
                    return;
                }
                
                AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort, false, delegate
                {
                    SupportFragmentManager.BeginTransaction()
                        .SetReorderingAllowed(true)
                        .SetCustomAnimations(Resource.Animation.fadein, Resource.Animation.fadeout)
                        .Replace(Resource.Id.viewFragment, _listFragment)
                        .Commit();
                });

                ReleaseOnCreateLock();
            });
        }

        protected override async void OnResume()
        {
            base.OnResume();

            await _onCreateLock.WaitAsync();
            _onCreateLock.Release();

            try
            {
                await FindServerNode();
            }
            catch (ApiException e)
            {
                Logger.Error(e);
                RunOnUiThread(CheckOfflineState);
                return;
            }

            try
            {
                await Refresh();
            }
            catch (Exception e)
            {
                Logger.Error(e);
                RunOnUiThread(delegate { Toast.MakeText(this, Resource.String.syncFailed, ToastLength.Short).Show(); });
            }

            RunOnUiThread(delegate
            {
                AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort, false, CheckOfflineState);
            });
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ReleaseOnCreateLock();
        }

        private void ReleaseOnCreateLock()
        {
            if (_onCreateLock.CurrentCount == 0)
            {
                _onCreateLock.Release();
            }
        }

        private void InitViews()
        {
            _circularProgressLayout = FindViewById<CircularProgressLayout>(Resource.Id.layoutCircularProgress);
            _offlineLayout = FindViewById<LinearLayout>(Resource.Id.layoutOffline);

            _categoryList = FindViewById<WearableNavigationDrawerView>(Resource.Id.drawerCategories);
            _categoryListAdapter = new CategoryListAdapter(this, _categoryView);
            _categoryList.SetAdapter(_categoryListAdapter);
            _categoryList.ItemSelected += OnCategorySelected;

            if (_authView.CategoryId != null)
            {
                var categoryPosition = _categoryView.FindIndex(c => c.Id == _authView.CategoryId);

                if (categoryPosition > -1)
                {
                    _preventCategorySelectEvent = true;
                    _categoryList.SetCurrentItem(categoryPosition + 1, false);
                }
            }
            else
            {
                _categoryList.SetCurrentItem(0, false);
            }
        }

        private void OnCategorySelected(object sender, WearableNavigationDrawerView.ItemSelectedEventArgs e)
        {
            if (_preventCategorySelectEvent)
            {
                _preventCategorySelectEvent = false;
                return;
            }

            if (e.Pos > 0)
            {
                var category = _categoryView.ElementAtOrDefault(e.Pos - 1);

                if (category == null)
                {
                    return;
                }

                _authView.CategoryId = category.Id;
            }
            else
            {
                _authView.CategoryId = null;
            }

            _listFragment.NotifyChanged();

            if (SupportFragmentManager.BackStackEntryCount > 0)
            {
                SupportFragmentManager.PopBackStack(); 
            }
        }
        
        private void OnItemClicked(object sender, int position)
        {
            var item = _authView.ElementAtOrDefault(position);

            if (item == null)
            {
                return;
            }

            var bundle = new Bundle();
            
            bundle.PutInt("type", (int) item.Type);
            bundle.PutString("issuer", item.Issuer);
            bundle.PutString("username", item.Username);
            bundle.PutString("issuer", item.Issuer);
            bundle.PutInt("period", item.Period);
            bundle.PutInt("digits", item.Digits);
            bundle.PutString("secret", item.Secret);
            bundle.PutString("pin", item.Pin);
            bundle.PutInt("algorithm", (int) item.Algorithm);
            
            var hasCustomIcon = !string.IsNullOrEmpty(item.Icon) && item.Icon.StartsWith(CustomIconCache.Prefix);
            bundle.PutBoolean("hasCustomIcon", hasCustomIcon);
            
            if (hasCustomIcon)
            {
                var id = item.Icon[1..];
                var bitmap = _customIconCache.GetCachedBitmap(id);
                bundle.PutParcelable("icon", bitmap);
            }
            else
            {
                bundle.PutString("icon", item.Icon);
            }
            
            var fragment = new CodeFragment();
            fragment.Arguments = bundle;

            SupportFragmentManager.BeginTransaction()
                .SetReorderingAllowed(true)
                .SetCustomAnimations(Resource.Animation.slidein, Resource.Animation.fadeout)
                .Add(Resource.Id.viewFragment, fragment)
                .AddToBackStack(null)
                .Commit();
        }

        private void CheckOfflineState()
        {
            if (_serverNode == null)
            {
                AnimUtil.FadeOutView(_circularProgressLayout, AnimUtil.LengthShort);
                _offlineLayout.Visibility = ViewStates.Visible;
            }
            else
            {
                _offlineLayout.Visibility = ViewStates.Invisible;
            }
        }

        private async Task FindServerNode()
        {
            var capabilityInfo = await WearableClass.GetCapabilityClient(this)
                .GetCapabilityAsync(ProtocolVersion, CapabilityClient.FilterReachable);

            _serverNode = capabilityInfo.Nodes.MaxBy(n => n.IsNearby);
        }

        private async Task Refresh()
        {
            if (_serverNode == null)
            {
                return;
            }

            var client = WearableClass.GetChannelClient(this);
            var channel = await client.OpenChannelAsync(_serverNode.Id, GetSyncBundlePath);

            InputStream stream = null;
            byte[] data;

            try
            {
                stream = await client.GetInputStreamAsync(channel);
                data = await StreamUtil.ReadAllBytesAsync(stream);
            }
            finally
            {
                stream.Close();
                await client.CloseAsync(channel);
            }

            var json = Encoding.UTF8.GetString(data);
            var bundle = JsonConvert.DeserializeObject<WearSyncBundle>(json);

            await OnSyncBundleReceived(bundle);
        }

        private async Task OnSyncBundleReceived(WearSyncBundle bundle)
        {
            var oldSortMode = _preferences.SortMode;
            var listChanged = false;

            if (oldSortMode != bundle.Preferences.SortMode)
            {
                _authView.SortMode = bundle.Preferences.SortMode;
                listChanged = true;
            }

            _preferences.ApplySyncedPreferences(bundle.Preferences);

            if (_authCache.Dirty(bundle.Authenticators, new WearAuthenticatorComparer()))
            {
                await _authCache.ReplaceAsync(bundle.Authenticators);
                _authView.Update();
                listChanged = true;
            }

            if (listChanged)
            {
                RunOnUiThread(_listFragment.NotifyChanged);
            }

            if (_categoryCache.Dirty(bundle.Categories, new WearCategoryComparer()))
            {
                await _categoryCache.ReplaceAsync(bundle.Categories);
                _categoryView.Update();
                RunOnUiThread(_categoryListAdapter.NotifyDataSetChanged);
            }

            var inCache = _customIconCache.GetIcons();
            var inBundle = bundle.CustomIcons.Select(i => i.Id).ToList();

            var toRemove = inCache.Where(i => !inBundle.Contains(i));

            foreach (var icon in toRemove)
            {
                _customIconCache.Remove(icon);
            }

            var toAdd = bundle.CustomIcons.Where(i => !inCache.Contains(i.Id));

            foreach (var icon in toAdd)
            {
                await _customIconCache.AddAsync(icon.Id, icon.Data);
            }
        }
    }
}