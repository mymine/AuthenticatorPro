// Copyright (C) 2025 jmh
// SPDX-License-Identifier: GPL-3.0-only

using System.Timers;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Wear.Widget;
using Java.Lang;
using Stratum.Core;
using Stratum.Core.Generator;
using Stratum.Core.Util;
using Stratum.Droid.Shared;
using Stratum.WearOS.Callback;
using Stratum.WearOS.Interface;
using Stratum.WearOS.Util;

namespace Stratum.WearOS.Fragment
{
    public class CodeFragment() : AndroidX.Fragment.App.Fragment(Resource.Layout.fragmentCode)
    {
        private IGenerator _generator;

        private int _codeGroupSize;
        private int _period;
        private int _digits;

        private AuthProgressLayout _authProgressLayout;
        private TextView _codeTextView;

        public override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var preferences = new PreferenceWrapper(RequireContext());
            _codeGroupSize = preferences.CodeGroupSize;
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            var view = base.OnCreateView(inflater, container, savedInstanceState);

            var swipeDismissLayout = view.FindViewById<SwipeDismissFrameLayout>(Resource.Id.layoutSwipeDismiss);
            
            var dismissCallback = new SwipeDismissCallback();
            dismissCallback.Dismiss += (_, _) =>
            {
                RequireActivity().SupportFragmentManager.PopBackStack();
                swipeDismissLayout.Visibility = ViewStates.Gone;      
            };

            swipeDismissLayout.AddCallback(dismissCallback);

            _authProgressLayout = view.FindViewById<AuthProgressLayout>(Resource.Id.layoutAuthProgress);
            _codeTextView = view.FindViewById<TextView>(Resource.Id.textCode);

            var issuerText = view.FindViewById<TextView>(Resource.Id.textIssuer);
            var usernameText = view.FindViewById<TextView>(Resource.Id.textUsername);

            var username = Arguments.GetString("username");
            var issuer = Arguments.GetString("issuer");

            issuerText.Text = issuer;

            if (string.IsNullOrEmpty(username))
            {
                usernameText.Visibility = ViewStates.Gone;
            }
            else
            {
                usernameText.Text = username;
            }

            var iconView = view.FindViewById<ImageView>(Resource.Id.imageIcon);
            var hasCustomIcon = Arguments.GetBoolean("hasCustomIcon");

            if (hasCustomIcon)
            {
                Bitmap bitmap;
                
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
#pragma warning disable CA1416
                    bitmap = (Bitmap) Arguments.GetParcelable("icon", Class.FromType(typeof(Bitmap)));
#pragma warning restore CA1416
                }
                else
                {
#pragma warning disable CA1422
                    bitmap = (Bitmap) Arguments.GetParcelable("icon");
#pragma warning restore CA1422
                }

                if (bitmap != null)
                {
                    iconView.SetImageBitmap(bitmap);
                }
                else
                {
                    iconView.SetImageResource(IconResolver.GetService(IconResolver.Default, true));
                }
            }
            else
            {
                iconView.SetImageResource(IconResolver.GetService(Arguments.GetString("icon"), true));
            }

            _period = Arguments.GetInt("period");
            _digits = Arguments.GetInt("digits");

            var algorithm = (HashAlgorithm) Arguments.GetInt("algorithm");

            var secret = Arguments.GetString("secret");
            var pin = Arguments.GetString("pin");

            var type = (AuthenticatorType) Arguments.GetInt("type");

            _generator = AuthenticatorUtil.GetGenerator(type, secret, pin, _period, algorithm, _digits);

            _authProgressLayout.Period = _period * 1000;
            _authProgressLayout.TimerFinished += Refresh;

            return view;
        }

        public override void OnResume()
        {
            base.OnResume();
            Refresh();
        }

        public override void OnPause()
        {
            base.OnPause();
            _authProgressLayout.StopTimer();
        }

        private void Refresh(object sender = null, ElapsedEventArgs args = null)
        {
            var (code, secondsRemaining) = AuthenticatorUtil.GetCodeAndRemainingSeconds(_generator, _period);
            _codeTextView.Text = CodeUtil.PadCode(code, _digits, _codeGroupSize);
            _authProgressLayout.StartTimer((_period - secondsRemaining) * 1000);
        }
    }
}