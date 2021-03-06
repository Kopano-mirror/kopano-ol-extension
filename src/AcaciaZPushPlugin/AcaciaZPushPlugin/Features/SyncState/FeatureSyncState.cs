﻿/// Copyright 2018 Kopano b.v.
/// 
/// This program is free software: you can redistribute it and/or modify
/// it under the terms of the GNU Affero General Public License, version 3,
/// as published by the Free Software Foundation.
/// 
/// This program is distributed in the hope that it will be useful,
/// but WITHOUT ANY WARRANTY; without even the implied warranty of
/// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
/// GNU Affero General Public License for more details.
/// 
/// You should have received a copy of the GNU Affero General Public License
/// along with this program.If not, see<http://www.gnu.org/licenses/>.
/// 
/// Consult LICENSE file for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Acacia.Stubs;
using Acacia.Utils;
using Acacia.ZPush;
using Acacia.Features.SharedFolders;
using Acacia.ZPush.API.SharedFolders;
using static Acacia.DebugOptions;
using Acacia.UI.Outlook;
using System.Drawing;
using Acacia.ZPush.Connect;
using Acacia.ZPush.Connect.Soap;
using Acacia.Features.GAB;
using Acacia.Features.Signatures;
using Acacia.UI;
using System.Windows.Forms;
using System.Collections.Concurrent;

// Prevent field assignment warnings
#pragma warning disable 0649

namespace Acacia.Features.SyncState
{   

    public class FeatureSyncState : Feature, FeatureWithRibbon
    {

        #region Sync configuration

        [AcaciaOption("Sets the period to check synchronisation state if a sync is in progress.")]
        public TimeSpan CheckPeriodSync
        {
            get { return GetOption(OPTION_CHECK_PERIOD_SYNC); }
            set { SetOption(OPTION_CHECK_PERIOD_SYNC, value); }
        }
        private static readonly TimeSpanOption OPTION_CHECK_PERIOD_SYNC = new TimeSpanOption("CheckPeriodSync", new TimeSpan(0, 1, 0));

        [AcaciaOption("Sets the period to check synchronisation state if a sync is in progress and the dialog is open.")]
        public TimeSpan CheckPeriodDialogSync
        {
            get { return GetOption(OPTION_CHECK_PERIOD_DIALOG_SYNC); }
            set { SetOption(OPTION_CHECK_PERIOD_DIALOG_SYNC, value); }
        }
        private static readonly TimeSpanOption OPTION_CHECK_PERIOD_DIALOG_SYNC = new TimeSpanOption("CheckPeriodDialogSync", new TimeSpan(0, 0, 30));

        [AcaciaOption("Sets the period to check synchronisation state if a sync is NOT in progress and the dialog is open.")]
        public TimeSpan CheckPeriodDialogNoSync
        {
            get { return GetOption(OPTION_CHECK_PERIOD_DIALOG_NO_SYNC); }
            set { SetOption(OPTION_CHECK_PERIOD_DIALOG_NO_SYNC, value); }
        }
        private static readonly TimeSpanOption OPTION_CHECK_PERIOD_DIALOG_NO_SYNC = new TimeSpanOption("CheckPeriodDialogNoSync", new TimeSpan(0, 5, 0));

        private bool _isSyncing;
        public TimeSpan CheckPeriodDialogEffective
        {
            get { return _isSyncing ? CheckPeriodDialogSync : CheckPeriodDialogNoSync; }
        }


        [AcaciaOption("Enables or disables checking for stalled synchronsation. If this is detected, a full resynchronisation " +
                      "is suggested automatically")]
        public bool CheckSyncStall
        {
            get { return GetOption(OPTION_CHECK_SYNC_STALL); }
            set { SetOption(OPTION_CHECK_SYNC_STALL, value); }
        }
        private static readonly BoolOption OPTION_CHECK_SYNC_STALL = new BoolOption("CheckSyncStall", true);

        [AcaciaOption("Sets the period that triggers a full resynchronisation suggestion. Note that the check is only performed " +
                      "when the synchronisation state is being updated, so the effective period may be longer than this.")]
        public TimeSpan CheckSyncStallPeriod
        {
            get { return GetOption(OPTION_CHECK_SYNC_STALL_PERIOD); }
            set { SetOption(OPTION_CHECK_SYNC_STALL_PERIOD, value); }
        }
        private static readonly TimeSpanOption OPTION_CHECK_SYNC_STALL_PERIOD = new TimeSpanOption("CheckSyncStallPeriod", new TimeSpan(0, 10, 0));
        /// <summary>
        /// The time at which a sync stall was first suspected, or null if this is not the case.
        /// </summary>
        private DateTime? _syncStallStarted;
        private bool _syncStallAsked;
        private DateTime? _syncStallLastSyncTime;

        #endregion

        // TODO: this is largely about progress bars, separate that?
        private class SyncStateData : DataProvider
        {
            private FeatureSyncState _feature;

            private int _syncProgressPercent = 100;
            public int SyncProgressPercent
            {
                get { return _syncProgressPercent; }
                set
                {
                    if (value != _syncProgressPercent)
                    {
                        _syncProgressPercent = Math.Max(0, Math.Min(100, value));

                        // Percentage has changed, update required
                        // If the dialog is open, force the update, otherwise it'll trigger only when the main window is active
                        _feature._button.Invalidate(_feature._dialog != null);
                    }
                }
            }

            public SyncStateData(FeatureSyncState feature)
            {
                this._feature = feature;
            }

            private static readonly Bitmap[] PROGRESS = CreateProgressImages();

            private const int PROGRESS_STEPS = 20;

            private static Bitmap[] CreateProgressImages()
            {
                Bitmap[] images = new Bitmap[PROGRESS_STEPS + 1];

                Bitmap img0 = Properties.Resources.Progress0;
                Bitmap img1 = Properties.Resources.Progress1;
                images[0] = img0;
                images[PROGRESS_STEPS] = img1;

                // Create a series of images starting with img0, overlayed with part of img1. This shows the progress bar filling up.
                for (int i = 1; i < PROGRESS_STEPS; ++i)
                {
                    Bitmap img = new Bitmap(img0);
                    using (var canvas = Graphics.FromImage(img))
                    {
                        int w = img1.Width * i / PROGRESS_STEPS;
                        Rectangle r = new Rectangle(0, 0, w, img0.Height);
                        canvas.DrawImage(img1, r, r, GraphicsUnit.Pixel);
                        canvas.Save();
                    }

                    images[i] = img;
                }

                return images;
            }

            public Bitmap GetImage(string elementId, bool large)
            {
                double round = (double)PROGRESS_STEPS / 100;
                int val = (int)Math.Floor((_syncProgressPercent / (double)100) * PROGRESS_STEPS + round);
                int index = Math.Max(0, Math.Min(PROGRESS_STEPS, val));

                // extra safety check, just in case
                return (index >= 0 && index <= PROGRESS_STEPS) ? PROGRESS[index] : PROGRESS[0];
            }

            public string GetLabel(string elementId)
            {
                if (SyncProgressPercent == 100)
                    return Properties.Resources.Ribbon_SyncState_Label_Done;
                return string.Format(Properties.Resources.Ribbon_SyncState_Label, SyncProgressPercent);
            }

            public string GetScreenTip(string elementId)
            {
                return Properties.Resources.Ribbon_SyncState_Screentip;
            }

            public string GetSuperTip(string elementId)
            {
                return Properties.Resources.Ribbon_SyncState_Supertip;
            }
        }

        private RibbonButton _button;
        private SyncStateData _state;
        private ZPushSync.SyncTask _task;

        public FeatureSyncState()
        {
        }

        public override void Startup()
        {
            _state = new SyncStateData(this);
            _button = RegisterButton(this, "Progress", true, ShowSyncState, ZPushBehaviour.None);
            _button.DataProvider = _state;
            // Add a sync task to start checking. If this finds it's not fully synchronised, it will check more often
            _task = Watcher.Sync.AddTask(this, Name, CheckSyncState);
        }

        private class DeviceDetails : ISoapSerializable<DeviceDetails.Data>
        {
            public class SyncData
            {
                public string status;
                public long total;
                public long done;
                public long todo;

                override public string ToString()
                {
                    return string.Format("{0}: {1}/{2}={3}", status, total, done, todo);
                }

                internal void MakeDone()
                {
                    done = total;
                    todo = 0;
                }
            }

            [SoapField]
            public struct ContentData
            {
                [SoapField(1)]
                public string synckey;

                [SoapField(2)]
                public OutlookConstants.SyncType type;

                [SoapField(3)]
                public string[] flags;

                [SoapField(4)]
                public SyncData Sync;

                public bool IsSyncing { get { return Sync != null && Sync.todo > 0; } }

                [SoapField(5)]
                public string id; 

                /// <summary>
                /// The key, which is a short folder id. Set when checking the values.
                /// </summary>
                public string Key
                {
                    get;
                    set;
                }
            }

            public struct Data2
            {
                public string deviceid;
                public string devicetype;
                public string domain;
                public string deviceuser;
                public string useragent;
                public DateTime firstsynctime;
                public string announcedasversion;
                public string hierarchyuuid;

                public string asversion;
                public DateTime lastupdatetime;
                public string koeversion;
                public string koebuild;
                public DateTime koebuilddate;
                public string[] koecapabilities;
                public string koegabbackendfolderid;

                public bool changed;
                public DateTime lastsynctime;
                public bool hasfolderidmapping;

                public Dictionary<string, ContentData> contentdata;

                // TODO: additionalfolders
                // TODO: hierarchycache
                // TODO: useragenthistory
            }

            public struct Data
            {
                public Data2 data;
                public bool changed;
            }

            private readonly Data _data;

            public DeviceDetails(Data data)
            {
                this._data = data;
            }

            public Data SoapSerialize()
            {
                return _data;
            }

            public Dictionary<string, ContentData> Content
            {
                get { return _data.data.contentdata; } 
            }

            public DateTime LastSyncTime
            {
                get { return _data.data.lastsynctime; }
            }
        }

        private class GetDeviceDetailsRequest : SoapRequest<DeviceDetails>
        {
        }

        /// <summary>
        /// Information stored per account on a synchronisation session.
        /// </summary>
        private class SyncSession
        {
            public long Total { get; private set; }
            public long Done { get; private set; }
            public bool IsSyncing { get; private set; }
            public DateTime? LastSyncTime { get; private set; }

            private readonly FeatureSyncState _feature;
            private readonly ZPushAccount _account;
            private readonly Dictionary<string, DeviceDetails.ContentData> _syncContent = new Dictionary<string, DeviceDetails.ContentData>();

            public SyncSession(FeatureSyncState feature, ZPushAccount account)
            {
                this._feature = feature;
                this._account = account;
            }

            /// <summary>
            /// Adds the details to the current session
            /// </summary>
            public void Add(DeviceDetails details)
            {
                StringBuilder debug = new StringBuilder();

                LastSyncTime = details.LastSyncTime;

                // If a folder is no longer reported as it's already synced, we don't necessarily get the
                // last step where (done == total). This causes some folders to keep lingering. To clean these
                // up, keep a list of folders syncing in this iteration.
                // Any folders in the session but not this list are done
                HashSet<string> syncingNow = new HashSet<string>();

                // Check all syncing data
                foreach (KeyValuePair<string, DeviceDetails.ContentData> contentEntry in details.Content)
                {
                    DeviceDetails.ContentData content = contentEntry.Value;
                    content.Key = contentEntry.Key;

                    if (content.IsSyncing)
                    {
                        // If the current session is not syncing, this is a restart. Clear stat
                        if (!IsSyncing)
                        {
                            _syncContent.Clear();
                            debug.AppendLine("Starting new SyncSession");
                        }

                        // Add to the syncing content
                        IsSyncing = true;
                        _syncContent[content.Key] = content;
                        syncingNow.Add(content.Key);

                        debug.AppendLine(string.Format("\tFolder: {0} \tSync: {1} \tStatus: {2} / {3}",
                                    content.Key, content.IsSyncing, content.Sync.done, content.Sync.total));
                    }
                }

                // Clean up any done items
                bool _syncingNow = false;
                foreach (DeviceDetails.ContentData content in _syncContent.Values)
                {
                    if (!syncingNow.Contains(content.Key))
                    {
                        content.Sync.MakeDone();
                    }
                    else
                    {
                        _syncingNow = true;
                    }
                }
                IsSyncing = _syncingNow;
                debug.AppendLine(string.Format("Calculating totals: ({0})", IsSyncing));

                // Update totals
                Total = 0;
                Done = 0;
                foreach(DeviceDetails.ContentData content in _syncContent.Values)
                {
                    Total += content.Sync.total;
                    Done += content.Sync.done;
                    debug.AppendLine(string.Format("\tFolder: {0} \tSync: {1} \tStatus: {2} / {3}",
                                content.Key, content.IsSyncing, content.Sync.done, content.Sync.total));
                }
                debug.AppendLine(string.Format("Total: {0} / {1} ({2}%)", Done, Total, CalculatePercentage(Done, Total)));
                Logger.Instance.Trace(_feature, "Syncing account {0}:\n{1}", _account, debug);
            }

            public bool HasFolderSynced(string folderId)
            {
                DeviceDetails.ContentData content;
                if (!_syncContent.TryGetValue(folderId, out content))
                    return false;

                return !string.IsNullOrWhiteSpace(content.synckey);
            }
        }


        private class UserStoreInfo
        {
            public string emailaddress;
            public string fullname;
            public long foldercount;
            public long storesize;

            public override string ToString()
            {
                return string.Format("emailaddress={0}\nfullname={1}\nfoldercount={2}\nstoresize={3}",
                    emailaddress, fullname, foldercount, storesize);
            }
        }
        private class GetUserStoreInfoRequest : SoapRequest<UserStoreInfo>
        {
        }

        private void CheckSyncState(ZPushAccount account)
        {
            // TODO: we probably want one invocation for all accounts
            using (ZPushConnection connection = account.Connect())
            {
                // Check total size
                CheckTotalSize(connection);

                // Update sync state
                using (ZPushWebServiceDevice deviceService = connection.DeviceService)
                {
                    // Fetch
                    DeviceDetails details = deviceService.Execute(new GetDeviceDetailsRequest());
                    if (details != null)
                    {
                        bool wasSyncing = false;

                        // Create or update session
                        SyncSession session = account.GetFeatureData<SyncSession>(this, null);
                        if (session == null)
                            session = new SyncSession(this, account);
                        else
                            wasSyncing = session.IsSyncing;

                        session.Add(details);

                        // Store with the account
                        account.SetFeatureData(this, null, session);

                        if (wasSyncing != session.IsSyncing)
                        {
                            // Sync state has changed, update the schedule
                            Watcher.Sync.SetTaskSchedule(_task, account, session.IsSyncing ? CheckPeriodSync : (TimeSpan?)null);
                        }
                    }
                }
            }

            // Update the total for all accounts
            UpdateTotalSyncState();

            // Check for stalls
            CheckSyncStalled(account);
        }

        private void UpdateTotalSyncState()
        {
            long total = 0;
            long done = 0;
            bool isSyncing = false;

            foreach(ZPushAccount account in Watcher.Accounts.GetAccounts())
            {
                SyncSession sync = account.GetFeatureData<SyncSession>(this, null);
                if (sync != null)
                {
                    total += sync.Total;
                    done += sync.Done;
                    if (sync.IsSyncing)
                        isSyncing = true;
                }
            }

            // Update UI
            _state.SyncProgressPercent = CalculatePercentage(done, total);
            if (_dialog != null)
                _dialog.RefreshData();

            if (_isSyncing != isSyncing)
            {
                _isSyncing = isSyncing;
                if(_dialog != null)
                {
                    // Update the task schedule
                    Watcher.Sync.SetTaskSchedule(_task, null, CheckPeriodDialogEffective, false);
                }
            }
        }

        private void CheckSyncStalled(ZPushAccount account)
        {
            if (!CheckSyncStall)
                return;

            // Check the inbox folder
            using (IFolder inbox = account.Account.Store.GetDefaultFolder(DefaultFolder.Inbox))
            {
                string syncId = (string)inbox.GetProperty(OutlookConstants.PR_ZPUSH_BACKEND_ID);

                // If it's syncing, it's not stalled
                if (syncId != null && syncId != "0")
                    return;

                SyncSession sync = account.GetFeatureData<SyncSession>(this, null);

                // If the last sync time hasn't changed, it hasn't actually synced yet
                if (sync.LastSyncTime != null && _syncStallLastSyncTime == sync.LastSyncTime)
                    return;
                _syncStallLastSyncTime = sync.LastSyncTime;

                // Get the sync state
                string folderId = (string)inbox.GetProperty(OutlookConstants.PR_ZPUSH_SYNC_ID);
                if (folderId != null)
                {

                    // Check if the folder has synced. In that case, it's not stalled.
                    if (sync.HasFolderSynced(folderId))
                      return;
                }
            }

            // It is not syncing, check for a stall
            if (_syncStallStarted == null)
                _syncStallStarted = DateTime.Now;
            else if (_syncStallStarted.Value.Add(CheckSyncStallPeriod) <= DateTime.Now)
            {
                // We have a stall
                if (!_syncStallAsked)
                {
                    // Set the flag to prevent asking again
                    _syncStallAsked = true;

                    // And alert the user
                    SyncStalled(account);
                }
            }
        }

        private void SyncStalled(ZPushAccount account)
        {
            ThisAddIn.Instance.InUI(() =>
            {
                if (MessageBox.Show(ThisAddIn.Instance.Window,
                    string.Format(Properties.Resources.SyncState_Stalled_Body, account.DisplayName),
                    string.Format(Properties.Resources.SyncState_Stalled_Caption, account.DisplayName),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Error
                    ) == DialogResult.Yes)
                {
                    IRestarter restarter = ThisAddIn.Instance.Restarter();
                    restarter.ResyncAccounts(account);
                    restarter.Restart();
                }
            });
        }

        private static int CalculatePercentage(long done, long total)
        {
            if (total == 0 || done == total)
                return 100;

            return Math.Max(0, Math.Min(100, (int)(done * 100 / total)));
        }

        private SyncStateDialog _dialog;

        private void ShowSyncState()
        {
            // Only show the dialog once
            if (_dialog != null)
                return;

            // Get the current account. If no Z-Push account is selected, the dialog will open for all accounts.
            ZPushAccount account = Watcher.CurrentZPushAccount();

            // Ramp up the checking schedule while the dialog is open
            // The other check sets per-account schedules, we use the global one, so they should't interfere.
            TimeSpan? old = Watcher.Sync.SetTaskSchedule(_task, null, CheckPeriodDialogEffective, true);
            SyncStateDialog dlg = new SyncStateDialog(this, account);
            dlg.FormClosed += (s, e) =>
            {
                // Restore the schedule
                Watcher.Sync.SetTaskSchedule(_task, null, old);
                _dialog = null;
            };

            // Show the dialog as a non-modal, otherwise the ribbon doesn't get updated
            _dialog = dlg;
            dlg.ShowCentered(ThisAddIn.Instance.Window);
        }

        private class SyncStateImpl : SyncState
        {
            private readonly FeatureSyncState _feature;
            private readonly ZPushAccount[] _accounts;
            private readonly bool[] _canResync;

            // Additional features for syncing
            private readonly FeatureGAB _featureGAB = ThisAddIn.Instance.GetFeature<FeatureGAB>();
            private readonly FeatureSignatures _featureSignatures = ThisAddIn.Instance.GetFeature<FeatureSignatures>();

            public SyncStateImpl(FeatureSyncState feature, ZPushAccount[] accounts)
            {
                this._feature = feature;
                this._accounts = accounts;
                _canResync = new bool[Enum.GetNames(typeof(ResyncOption)).Length];
                _canResync[(int)ResyncOption.GAB] = _featureGAB != null;
                _canResync[(int)ResyncOption.Signatures] = _featureSignatures != null;
                _canResync[(int)ResyncOption.ServerData] = ThisAddIn.Instance.Watcher.Sync.Enabled;
                _canResync[(int)ResyncOption.Full] = true;
            }

            public long Done
            {
                get;
                private set;
            }

            public bool IsSyncing
            {
                get;
                private set;
            }

            public long Remaining
            {
                get;
                private set;
            }

            public long Total
            {
                get;
                private set;
            }

            public int Percentage
            {
                get { return CalculatePercentage(Done, Total); }
            }

            public bool CanResync(ResyncOption option)
            {
                // TODO: check if outlook is not offline?
                return _canResync[(int)option];
            }

            public bool Resync(ResyncOption option)
            {
                if (!CanResync(option))
                    return true;

                switch(option)
                {
                    case ResyncOption.GAB:
                        if (IsSyncing)
                        {
                            // Already syncing, resync GAB asynchronously
                            _featureGAB.FullResync(null, _accounts);
                            // Cannot resync again until the dialog is reopened
                            _canResync[(int)ResyncOption.GAB] = false;
                            return false;
                        }
                        else
                        {
                            ProgressDialog.Execute("GABSync",
                                (ct, tracker) =>
                                {
                                    _featureGAB.FullResync(tracker, _accounts);
                                    return 0;
                                }
                            );
                            return true;
                        }
                    case ResyncOption.Signatures:
                        ProgressDialog.Execute("SignaturesSync",
                            (ct) =>
                            {
                                _featureSignatures.Resync(_accounts);
                                return 0;
                            }
                        );
                        return true;
                    case ResyncOption.ServerData:
                        ProgressDialog.Execute("ServerSync",
                            (ct) =>
                            {
                                ThisAddIn.Instance.Watcher.Sync.ExecuteTasks(_accounts);
                                return 0;
                            }
                        );
                        
                        return true; 
                    case ResyncOption.Full:
                        IRestarter restarter = ThisAddIn.Instance.Restarter();
                        restarter.ResyncAccounts(_accounts);
                        restarter.Restart();
                        return true;
                }
                return true;
            }

            public void Update()
            {
                Total = 0;
                Done = 0;
                IsSyncing = false;

                foreach (ZPushAccount account in _accounts)
                {
                    SyncSession sync = account.GetFeatureData<SyncSession>(_feature, null);
                    if (sync != null)
                    {
                        Total += sync.Total;
                        Done += sync.Done;
                        if (sync.IsSyncing)
                            IsSyncing = true;
                    }
                }

                Remaining = Total - Done;
            }
        }

        /// <summary>
        /// Returns a SyncState for the specified account, or all accounts.
        /// </summary>
        /// <param name="account">The account, or null to fetch a SyncState for all accounts</param>
        public SyncState GetSyncState(ZPushAccount account)
        {
            return new SyncStateImpl(this, account == null ? Watcher.Accounts.GetAccounts().ToArray() : new ZPushAccount[] { account });
        }

        public bool SupportsSyncTimeFrame(ZPushAccount account)
        {
            return account?.ZPushVersion?.IsAtLeast(2, 4) == true;
        }

        private class SetDeviceOptionsRequest : SoapRequest<bool>
        {
            public SetDeviceOptionsRequest(SyncTimeFrame timeFrame)
            {
                Parameters.Add("filtertype", (int)timeFrame);
            }
        }

        public void SetDeviceOptions(ZPushAccount account, SyncTimeFrame timeFrame)
        {
            try
            {
                Logger.Instance.Debug(this, "Setting sync time frame for {0} to {1}", account, timeFrame);

                // First set the server value.
                using (ZPushConnection connection = account.Connect())
                using (ZPushWebServiceDevice deviceService = connection.DeviceService)
                {
                    deviceService.Execute(new SetDeviceOptionsRequest(timeFrame));
                }

                // And the local value
                account.SyncTimeFrame = timeFrame;
                Logger.Instance.Debug(this, "Set sync time frame for {0} to {1}", account, timeFrame);

                // Sync
                ThisAddIn.Instance.SendReceive(account.Account);
            }
            catch (Exception x)
            {
                Logger.Instance.Warning(this, "Exception setting sync time frame for {0} to {1}: {2}", account, timeFrame, x);
            }


        }


        #region Size checking

        [AcaciaOption("Disable checking of store size to suggest a sync time frame.")]
        public bool CheckStoreSize
        {
            get { return GetOption(OPTION_CHECK_STORE_SIZE); }
            set { SetOption(OPTION_CHECK_STORE_SIZE, value); }
        }
        private static readonly BoolOption OPTION_CHECK_STORE_SIZE = new BoolOption("CheckStoreSize", true);

        private bool _checkedStoreSize = false;

        private const string KEY_CHECKED_SIZE = "KOE Size Checked";

        private void CheckTotalSize(ZPushConnection connection)
        {
            if (!CheckStoreSize || _checkedStoreSize)
                return;

            // Only works on 2.5
            // If we don't have the version yet, try again later
            if (connection.Account.ZPushVersion == null)
                return;

            // Only check once
            _checkedStoreSize = true;

            // If it's not 2.5, don't check again
            if (!connection.Account.ZPushVersion.IsAtLeast(2, 5))
                return;

            try
            {
                Logger.Instance.Debug(this, "Fetching size information for account {0}", connection.Account);
                using (ZPushWebServiceInfo infoService = connection.InfoService)
                {
                    UserStoreInfo info = infoService.Execute(new GetUserStoreInfoRequest());
                    Logger.Instance.Debug(this, "Size information: {0}", info);
                    SyncTimeFrame suggested = SuggestSyncTimeFrame(info);

                    if (suggested.IsShorterThan(connection.Account.SyncTimeFrame))
                    {
                        // Suggest shorter time frame, if not already done
                        string lastCheckedSize = connection.Account.Account[KEY_CHECKED_SIZE];
                        if (!string.IsNullOrWhiteSpace(lastCheckedSize))
                        {
                            try
                            {
                                SyncTimeFrame old = (SyncTimeFrame)int.Parse(lastCheckedSize);
                                if (old >= suggested)
                                {
                                    Logger.Instance.Trace(this, "Not suggesting reduced sync time frame again: {0}: {2} -> {1}", info, suggested, old);
                                    return;
                                }
                            }
                            catch(Exception e)
                            {
                                Logger.Instance.Warning(this, "Invalid lastCheckedSize: {0}: {1}", lastCheckedSize, e);
                            }
                        }

                        Logger.Instance.Debug(this, "Suggesting reduced sync time frame: {0}: {2} -> {1}", info, suggested, connection.Account.SyncTimeFrame);

                        // Suggest a shorter timeframe
                        DialogResult result = MessageBox.Show(ThisAddIn.Instance.Window,
                            string.Format(Properties.Resources.SyncState_StoreSize_Body, 
                                info.storesize.ToSizeString(SizeUtil.Size.MB), 
                                suggested.ToDisplayString()), 
                            Properties.Resources.SyncState_StoreSize_Caption,
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

                        if (result == DialogResult.OK)
                        {
                            // Set the sync time frame
                            Logger.Instance.Debug(this, "Applying reduced sync time frame: {0}: {2} -> {1}", info, suggested, connection.Account.SyncTimeFrame);
                            connection.Account.SyncTimeFrame = suggested;
                        }
                    }

                    connection.Account.Account[KEY_CHECKED_SIZE] = ((int)suggested).ToString();
                }
            }
            catch(Exception e)
            {
                Logger.Instance.Warning(this, "Error suggesting size: {0}", e);
            }
        }

        private struct SuggestedSyncTimeFrame
        {
            public long storeSize;
            public SyncTimeFrame timeFrame;

            public SuggestedSyncTimeFrame(long storeSize, SyncTimeFrame timeFrame) : this()
            {
                this.storeSize = storeSize;
                this.timeFrame = timeFrame;
            }
        }

        private static readonly SuggestedSyncTimeFrame[] SUGGESTED_SYNC_TIME_FRAMES =
        {
            new SuggestedSyncTimeFrame(10.WithSize(SizeUtil.Size.GB), SyncTimeFrame.MONTH_1),
            new SuggestedSyncTimeFrame(5.WithSize(SizeUtil.Size.GB), SyncTimeFrame.MONTH_3),
            new SuggestedSyncTimeFrame(2.WithSize(SizeUtil.Size.GB), SyncTimeFrame.MONTH_6)
        };

        private SyncTimeFrame SuggestSyncTimeFrame(UserStoreInfo info)
        {
            foreach(SuggestedSyncTimeFrame suggested in SUGGESTED_SYNC_TIME_FRAMES)
            {
                if (info.storesize >= suggested.storeSize)
                    return suggested.timeFrame;
            }
            return SyncTimeFrame.ALL;
        }

        #endregion
    }

}
