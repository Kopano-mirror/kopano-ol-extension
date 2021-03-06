﻿/// Copyright 2016 Kopano b.v.
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
using Acacia.Stubs.OutlookWrappers;
using System.Threading;
using System.Collections.Concurrent;
using Acacia.ZPush;
using Acacia.Utils;
using System.ComponentModel;
using System.Windows.Forms;
using Acacia.UI;
using static Acacia.DebugOptions;

namespace Acacia.Features.GAB
{
    [AcaciaOption("Provides a Global Address Book for Z-Push accounts.")]
    public class FeatureGAB : Feature
    {
        private readonly Dictionary<string, GABHandler> _gabsByDomainName = new Dictionary<string, GABHandler>();
        private readonly HashSet<string> _gabFolders = new HashSet<string>();
        private readonly HashSet<string> _domains = new HashSet<string>();
        private IStore _store;
        private int _processing;

        public FeatureGAB()
        {
        }

        public delegate void GABSyncFinishedHandler(GABHandler gab);
        public event GABSyncFinishedHandler SyncFinished;

        public void OnGabSyncFinished(GABHandler gab)
        {
            SyncFinished?.Invoke(gab);
        }

        public static GABHandler FindGABForAccount(ZPushAccount account)
        {
            FeatureGAB gab = ThisAddIn.Instance.GetFeature<FeatureGAB>();
            if (gab != null)
            {
                foreach (GABHandler handler in gab.GABHandlers)
                {
                    if (account == handler.ActiveAccount)
                    {
                        return handler;
                    }
                }
            }
            return null;
        }

        public override void Startup()
        {
            SetupModificationSuppression();
            Watcher.AccountDiscovered += AccountDiscovered;
            Watcher.AccountRemoved += AccountRemoved;
            Watcher.AccountsScanned += AccountsScanned;
        }

        private void SetupModificationSuppression()
        {
            if (MailEvents == null)
                return;

            // GAB modification suppression
            if (SuppressModifications)
            {
                MailEvents.BeforeDelete += SuppressEventHandler_Delete;
                MailEvents.PropertyChange += SuppressEventHandler_PropertyChange;
                MailEvents.Write += SuppressEventHandler_Write;
            }
        }

        #region Settings

        public override FeatureSettings GetSettings()
        {
            return new GABSettings(this);
        }

        [AcaciaOption("If set, fax numbers are synchronised. This is normally disabled as Outlook interprets them as email addresses. ")]
        public bool SyncFaxNumbers
        {
            get { return GetOption(OPTION_SYNC_FAX_NUMBERS); }
            set { SetOption(OPTION_SYNC_FAX_NUMBERS, value); }
        }
        private static readonly BoolOption OPTION_SYNC_FAX_NUMBERS = new BoolOption("SyncFaxNumbers", false);

        #endregion

        #region Debug options

        [AcaciaOption("Disables the processing of the folder containing the GAB synchronization queue. " +
                      "This should only be disabled for debug purposes.")]
        public bool ProcessFolder
        {
            get { return GetOption(OPTION_PROCESS_FOLDER); }
            set { SetOption(OPTION_PROCESS_FOLDER, value); }
        }
        private static readonly BoolOption OPTION_PROCESS_FOLDER = new BoolOption("ProcessFolder", true);

        [AcaciaOption("Disables the processing of items in the GAB synchronization queue. " +
                      "This should only be disabled for debug purposes.")]
        public bool ProcessItems
        {
            get { return GetOption(OPTION_PROCESS_ITEMS); }
            set { SetOption(OPTION_PROCESS_ITEMS, value); }
        }
        private static readonly BoolOption OPTION_PROCESS_ITEMS = new BoolOption("ProcessItems", true);

        [AcaciaOption("Disables the second stage of processing of items in the GAB synchronization queue. " +
                      "This should only be disabled for debug purposes")]
        public bool ProcessItems2
        {
            get { return GetOption(OPTION_PROCESS_ITEMS_2); }
            set { SetOption(OPTION_PROCESS_ITEMS_2, value); }
        }
        private static readonly BoolOption OPTION_PROCESS_ITEMS_2 = new BoolOption("ProcessItems2", true);

        [AcaciaOption("Disables the processing of messages containing GAB contacts. " +
                      "This should only be disabled for debug purposes.")]
        public bool ProcessMessage
        {
            get { return GetOption(OPTION_PROCESS_MESSAGE); }
            set { SetOption(OPTION_PROCESS_MESSAGE, value); }
        }
        private static readonly BoolOption OPTION_PROCESS_MESSAGE = new BoolOption("ProcessMessage", true);

        [AcaciaOption("If disabled, existing contacts are not deleted when a chunk is processed. " +
                      "This should only be disabled for debug purposes.")]
        public bool ProcessMessageDeleteExisting
        {
            get { return GetOption(OPTION_PROCESS_MESSAGE_DELETE_EXISTING); }
            set { SetOption(OPTION_PROCESS_MESSAGE_DELETE_EXISTING, value); }
        }
        private static readonly BoolOption OPTION_PROCESS_MESSAGE_DELETE_EXISTING = new BoolOption("ProcessMessageDeleteExisting", true);

        [AcaciaOption("If disabled, contacts are not created from incoming GAB messages. " +
                      "This should only be disabled for debug purposes.")]
        public bool CreateContacts
        {
            get { return GetOption(OPTION_CREATE_CONTACTS); }
            set { SetOption(OPTION_CREATE_CONTACTS, value); }
        }
        private static readonly BoolOption OPTION_CREATE_CONTACTS = new BoolOption("CreateContacts", true);

        [AcaciaOption("If disabled, groups are not created from incoming GAB messages. " +
                      "This should only be disabled for debug purposes.")]
        public bool CreateGroups
        {
            get { return GetOption(OPTION_CREATE_GROUPS); }
            set { SetOption(OPTION_CREATE_GROUPS, value); }
        }
        private static readonly BoolOption OPTION_CREATE_GROUPS = new BoolOption("CreateGroups", true);

        [AcaciaOption("If disabled, group members are not parsed from incoming GAB messages. " +
                      "This should only be disabled for debug purposes.")]
        public bool GroupMembers
        {
            get { return GetOption(OPTION_GROUP_MEMBERS); }
            set { SetOption(OPTION_GROUP_MEMBERS, value); }
        }
        private static readonly BoolOption OPTION_GROUP_MEMBERS = new BoolOption("GroupMembers", true);

        [AcaciaOption("If disabled, group members are not added to groups created from GAB messages. " +
                      "This should only be disabled for debug purposes.")]
        public bool GroupMembersAdd
        {
            get { return GetOption(OPTION_GROUP_MEMBERS_ADD); }
            set { SetOption(OPTION_GROUP_MEMBERS_ADD, value); }
        }
        private static readonly BoolOption OPTION_GROUP_MEMBERS_ADD = new BoolOption("GroupMembersAdd", true);

        [AcaciaOption("If disabled, groups that are members of other groups are not added to the parent group. " +
                      "This should only be disabled for debug purposes.")]
        public bool NestedGroups
        {
            get { return GetOption(OPTION_NESTED_GROUPS); }
            set { SetOption(OPTION_NESTED_GROUPS, value); }
        }
        private static readonly BoolOption OPTION_NESTED_GROUPS = new BoolOption("NestedGroups", true);

        [AcaciaOption("If this option is enabled, the GAB checks for unused local folders and removes them. " +
                      "If disabled, the unused local folders are left alone. The only reason GAB folders " +
                      "can become unused is if an account is removed, or if the GAB is removed from the server.")]
        public bool CheckUnused
        {
            get { return GetOption(OPTION_CHECK_UNUSED); }
            set { SetOption(OPTION_CHECK_UNUSED, value); }
        }
        private static readonly BoolOption OPTION_CHECK_UNUSED = new BoolOption("CheckUnused", true);

        [AcaciaOption("If disabled, existing contacts are not cleared before new contacts are created. " +
                      "This should only be disabled for debug purposes.")]
        public bool ClearContacts
        {
            get { return GetOption(OPTION_CLEAR_CONTACTS); }
            set { SetOption(OPTION_CLEAR_CONTACTS, value); }
        }
        private static readonly BoolOption OPTION_CLEAR_CONTACTS = new BoolOption("ClearContacts", true);

        [AcaciaOption("If disabled, existing contact folders are not deleted before new contacts are created. " +
                      "This should only be disabled for debug purposes.")]
        public bool DeleteExistingFolder
        {
            get { return GetOption(OPTION_DELETE_EXISTING_FOLDER); }
            set { SetOption(OPTION_DELETE_EXISTING_FOLDER, value); }
        }
        private static readonly BoolOption OPTION_DELETE_EXISTING_FOLDER = new BoolOption("DeleteExistingFolder", true);

        [AcaciaOption("If disabled, deleted items are not removed from the waste basket. " +
                      "This should only be disabled for debug purposes.")]
        public bool EmptyDeletedItems
        {
            get { return GetOption(OPTION_EMPTY_DELETED_ITEMS); }
            set { SetOption(OPTION_EMPTY_DELETED_ITEMS, value); }
        }
        private static readonly BoolOption OPTION_EMPTY_DELETED_ITEMS = new BoolOption("EmptyDeletedItems", true);

        [AcaciaOption("If enabled, modifications to the GAB folder are suppressed. " +
                      "This should only be disabled for debug purposes.")]
        public bool SuppressModifications
        {
            get { return GetOption(OPTION_SUPPRESS_MODIFICATIONS); }
            set { SetOption(OPTION_SUPPRESS_MODIFICATIONS, value); }
        }
        private static readonly BoolOption OPTION_SUPPRESS_MODIFICATIONS = new BoolOption("SuppressModifications", true);

        [AcaciaOption("If enabled, groups with an SMTP address are created as contacts.")]
        public bool SMTPGroupsAsContacts
        {
            get { return GetOption(OPTION_SMTP_GROUPS_AS_CONTACTS); }
            set { SetOption(OPTION_SMTP_GROUPS_AS_CONTACTS, value); }
        }
        private static readonly BoolOption OPTION_SMTP_GROUPS_AS_CONTACTS = new BoolOption("SMTPGroupsAsContacts", false);

        [AcaciaOption("If enabled, the FileAs field will be set to displayName. Otherwise, Outlook will create it.")]
        public bool FileAsDisplayName
        {
            get { return GetOption(OPTION_FILE_AS_DISPLAY_NAME); }
            set { SetOption(OPTION_FILE_AS_DISPLAY_NAME, value); }
        }
        private static readonly BoolOption OPTION_FILE_AS_DISPLAY_NAME = new BoolOption("FileAsDisplayName", false);

        [AcaciaOption("If enabled, an item cache will be used during GAB creation")]
        public bool ItemCache
        {
            get { return GetOption(OPTION_ITEM_CACHE); }
            set { SetOption(OPTION_ITEM_CACHE, value); }
        }
        private static readonly BoolOption OPTION_ITEM_CACHE = new BoolOption("ItemCache", false);

        #endregion

        #region Modification suppression

        internal void BeginProcessing()
        {
            ++_processing;
        }

        internal void EndProcessing()
        {
            --_processing;
        }

        private void SuppressEventHandler_Delete(IItem item, ref bool cancel)
        {
            SuppressEventHandler(item, false, ref cancel);
        }

        /// <summary>
        /// When copying a contact from the GAB to a local folder, Outlook raises the Write event on
        /// the original. To detect this, we set this id on every property change (which is signalled before
        /// write), and only suppress if anything has actually changed. If we suppress, the flag is cleared again.
        /// </summary>
        private string _propertyChangeId;

        private void SuppressEventHandler_PropertyChange(IItem item, string propertyName)
        {
            if (_propertyChangeId == item.EntryID)
                return;
            _propertyChangeId = item.EntryID;
        }

        private void SuppressEventHandler_Write(IItem item, ref bool cancel)
        {
            if (_propertyChangeId == item.EntryID)
            {
                SuppressEventHandler(item, true, ref cancel);
                _propertyChangeId = null;
            }
        }

        private void SuppressEventHandler(IItem item, bool findInspector, ref bool cancel)
        {
            // Allow events from processing
            if (_processing == 0)
            {
                // Check parent folder is a GAB contacts folder
                if (_gabFolders.Contains(item.ParentEntryID) && IsGABItem(item))
                {
                    DoSuppressEvent(findInspector ? item : null, ref cancel);
                }
            }
        }

        private bool IsGABItem(IItem item)
        {
            // For some reason, Outlook creates a meeting request in the GAB folder. Modifying that should be allowed
            return item is IContactItem || item is IDistributionList;
        }

        private void SuppressMoveEventHandler(IFolder src, IItem item, IFolder target, ref bool cancel)
        {
            // Allow events from processing
            if (_processing == 0)
            {
                // Always disallow moves from out of the folder
                DoSuppressEvent(null, ref cancel);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="item">If specified, the function will attempt to find an inspector for the item and close it.</param>
        /// <param name="cancel"></param>
        private void DoSuppressEvent(IItem item, ref bool cancel)
        {
            // TODO: Find and close the inspector
            /*if (item != null)
            {
                foreach (Inspector inspector in ThisAddIn.Instance.Inspectors)
                {
                    if (item.EntryId == inspector.CurrentItem.EntryID)
                    {
                        break;
                    }
                }
            }*/

            // Show message and cancel event
            // TODO: show on active inspector if used
            // [KOE-108] If the window is not specified as parent, it sometimes doesn't show
            MessageBox.Show(ThisAddIn.Instance.Window,
                            Properties.Resources.GABEvent_Body,
                            Properties.Resources.GABEvent_Title,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
            cancel = true;
        }

        #endregion

        #region Resync

        /// <summary>
        /// Performs a full resync.
        /// </summary>
        /// <param name="completion">The completion tracker, or null.</param>
        /// <param name="accounts">The accounts to resync, or null to resync all</param>
        internal void FullResync(CompletionTracker completion, ZPushAccount[] accounts)
        {
            try
            {
                // TODO: implement per-account resyncing
                Logger.Instance.Trace(this, "FullResync begin: {0}", _processing);
                BeginProcessing();

                // Delete any contacts folders in the local store
                if (DeleteExistingFolder)
                {
                    using (IStore store = ZPushLocalStore.GetInstance(ThisAddIn.Instance, this))
                    {
                        if (store != null)
                        {
                            using (IFolder root = store.GetRootFolder())
                            {
                                foreach (IFolder folder in root.GetSubFolders<IFolder>().DisposeEnum())
                                {
                                    try
                                    {
                                        if (IsGABContactsFolder(folder, accounts))
                                        {
                                            Logger.Instance.Debug(this, "FullResync: Deleting contacts folder: {0}", folder.Name);
                                            folder.Delete();
                                        }
                                    }
                                    catch (System.Exception e)
                                    {
                                        Logger.Instance.Error(this, "FullResync: Exception deleting contacts folder: {0}", e);
                                    }
                                }
                            }
                        }
                    }
                }

                // Do the resync
                using (completion.Begin())
                {
                    foreach (GABHandler gab in _gabsByDomainName.Values)
                    {
                        // Check if the gab is appropriate for the accounts
                        if (accounts == null || accounts.Contains(gab.ActiveAccount))
                        {
                            completion.Begin();
                            CompletionTracker partCompletion = new CompletionTracker(() =>
                            {
                                OnGabSyncFinished(gab);
                                completion.End();
                            });

                            Logger.Instance.Debug(this, "FullResync: Starting resync: {0}", gab.DisplayName);
                            Tasks.Task(partCompletion, this, "FullResync", () =>
                            {
                                gab.FullResync(partCompletion);
                            });
                        }
                    }
                }
            }
            finally
            {
                EndProcessing();
                Logger.Instance.Trace(this, "FullResync done: {0}", _processing);
            }
        }

        #endregion

        #region Contacts folders

        [Browsable(false)]
        public IEnumerable<GABHandler> GABHandlers
        {
            get
            {
                return _gabsByDomainName.Values;
            }
        }

        private IAddressBook FindGABForDomain(IFolder root, string domain)
        {
            // Scan all subfolders for the GAB
            foreach (IAddressBook subfolder in root.GetSubFolders<IAddressBook>())
            {
                try
                {
                    if (subfolder.DefaultMessageClass == OutlookConstants.MESSAGE_CLASS_CONTACTS)
                    {
                        GABInfo gabInfo = GABInfo.Get(subfolder);
                        if (gabInfo != null && gabInfo.IsForDomain(domain))
                        {
                            Logger.Instance.Debug(this, "Found existing GAB: {0}", gabInfo);
                            return subfolder;
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Logger.Instance.Warning(this, "Exception scanning GABs: {0}", e);
                }
                Logger.Instance.Debug(this, "Skipping GAB folder: {0}", subfolder.Name);
                subfolder.Dispose();
            }
            return null;
        }

        private IAddressBook CreateGABContacts(string domainName)
        {
            if (_store != null)
            {
                _store.Dispose();
                _store = null;
            }
            _store = ZPushLocalStore.GetInstance(ThisAddIn.Instance, this);
            if (_store == null)
                return null;

            // Try to find the existing GAB
            using (IFolder root = _store.GetRootFolder())
            {
                IAddressBook gab = FindGABForDomain(root, domainName);
                if (gab == null)
                {
                    Logger.Instance.Debug(this, "Creating new GAB folder for {0}", domainName);
                    string name = string.Format(Properties.Resources.GAB_FolderFormat, domainName);
                    gab = root.CreateFolder<IAddressBook>(name);
                }
                else
                {
                    Logger.Instance.Debug(this, "Found existing GAB folder for {0}", domainName);
                }

                // The local folders are hidden, unhide tha GAB folder
                gab.AttrHidden = false;

                // Update admin
                _gabFolders.Add(gab.EntryID);
                GABInfo gabInfo = GABInfo.Get(gab, domainName);
                gabInfo.Store(gab);

                if (SuppressModifications)
                {
                    // Hook BeforeMove event to prevent modifications
                    // TODO: use ZPushWatcher for this?
                    gab.BeforeItemMove += SuppressMoveEventHandler;
                }

                return gab;
            }
        }

        private void DisposeGABContacts(IAddressBook gab)
        {
            if (SuppressModifications)
            {
                // Unhook the event to prevent the gab lingering in memory
                gab.BeforeItemMove -= SuppressMoveEventHandler;
            }
        }

        public static GABInfo GetGABContactsFolderInfo(IFolder folder)
        {
            if (folder.DefaultMessageClass != OutlookConstants.MESSAGE_CLASS_CONTACTS)
                return null;

            return GABInfo.Get(folder);
        }

        /// <summary>
        /// Checks if the folder is a relevant GAB contacts folder.
        /// </summary>
        /// <param name="folder">The folder.</param>
        /// <param name="accounts">If specified, the folder is considered relevant only if it is the GAB for one of the specified
        /// accounts. Otherwise, any GAB folder is considered relevant.</param>
        public static bool IsGABContactsFolder(IFolder folder, ZPushAccount[] accounts)
        {
            // Check if this is a GAB folder at all
            GABInfo gab = GetGABContactsFolderInfo(folder);
            if (gab == null)
                return false;

            // If we don't have a list of accounts, it is what we're looking for
            if (accounts == null)
                return true;

            // Check if the domain is specified
            foreach(ZPushAccount account in accounts)
            {
                if (account.Account.DomainName == gab.Domain)
                    return true;
            }
            return false;
        }

        private void AccountDiscovered(ZPushAccount zpush)
        {
            Logger.Instance.Info(this, "Account discovered: {0}", zpush.DisplayName);
            _domains.Add(zpush.Account.DomainName);

            zpush.ConfirmedChanged += (z) =>
            {
                Logger.Instance.Debug(this, "Account discovered and confirmed: {0} -> {1}: {2}", zpush.DisplayName, zpush.Confirmed, zpush.GABFolder);
                if (zpush.Confirmed == ZPushAccount.ConfirmationType.IsZPush &&
                    !string.IsNullOrEmpty(zpush.GABFolder))
                {
                    Logger.Instance.Debug(this, "Account discovered, listening for folder: {0} -> {1}: {2}", zpush.DisplayName, zpush.Confirmed, zpush.GABFolder);
                    // Set up the Z-Push channel listener
                    ZPushChannel channel = ZPushChannels.Get(this, zpush, zpush.GABFolder);
                    channel.Available += ZPushChannelAvailable;
                    channel.Start();
                }
            };
        }

        private void AccountRemoved(ZPushAccount account)
        {
            try
            {
                foreach (GABHandler gab in _gabsByDomainName.Values)
                {
                    gab.RemoveAccount(account);
                }
                CheckGABRemoved();
            }
            catch (System.Exception e)
            {
                Logger.Instance.Error(this, "Exception in AccountRemoved: {0}", e);
            }
        }

        private void AccountsScanned()
        {
            try
            {
                Logger.Instance.Debug(this, "Accounts scanned");
                CheckGABUnused();
                CheckGABRemoved();
            }
            catch(System.Exception e)
            {
                Logger.Instance.Error(this, "Exception in AccountsScanned: {0}", e);
            }
        }

        /// <summary>
        /// Checks and removes any GAB folders that are not registered to accounts. This
        /// happens if an account is removed while Outlook is closed.
        /// </summary>
        private void CheckGABUnused()
        {
            if (!CheckUnused)
                return;

            if (_store != null)
            {
                _store.Dispose();
                _store = null;
            }
            _store = ZPushLocalStore.GetInstance(ThisAddIn.Instance, this);
            if (_store == null)
                return;

            bool deletedSomething = false;
            using (IFolder root = _store.GetRootFolder())
            {
                foreach (IFolder subfolder in root.GetSubFolders<IFolder>())
                {
                    using (subfolder)
                    {
                        // Remove any contacts folder that is not registered for GAB
                        GABInfo info = GetGABContactsFolderInfo(subfolder);
                        if (info != null && !_domains.Contains(info.Domain))
                        {
                            Logger.Instance.Info(this, "Unused GAB folder: {0} - {1}", subfolder.EntryID, subfolder.Name);
                            try
                            {
                                deletedSomething = true;
                                subfolder.Delete();
                            }
                            catch (System.Exception e)
                            {
                                Logger.Instance.Error(this, "Error removing GAB folder: {0}", e);
                            }
                        }
                    }
                }
            }

            if (deletedSomething)
                DoEmptyDeletedItems();
        }

        private void CheckGABRemoved()
        {
            Logger.Instance.Debug(this, "CheckGABRemoved");

            // Find any GABs that no longer have accounts
            List<KeyValuePair<string, GABHandler>> remove = new List<KeyValuePair<string, GABHandler>>();
            foreach(KeyValuePair<string, GABHandler> entry in _gabsByDomainName)
            {
                Logger.Instance.Debug(this, "CheckGABRemoved: {0} - {1}", entry.Key, entry.Value.HasAccounts);
                if (!entry.Value.HasAccounts)
                {
                    remove.Add(entry);
                }
            }

            // Remove any
            if (remove.Count != 0)
            {
                foreach (KeyValuePair<string, GABHandler> entry in remove)
                {
                    try
                    {
                        Logger.Instance.Info(this, "Removing GAB: {0}", entry.Key);
                        _gabsByDomainName.Remove(entry.Key);
                        entry.Value.Remove();
                    }
                    catch (System.Exception e)
                    {
                        Logger.Instance.Error(this, "Exception removing GAB: {0}: {1}", entry.Key, e);
                    }
                }

                DoEmptyDeletedItems();
            }
        }

        private void RegisterGABAccount(ZPushAccount account, IFolder folder)
        {
            // Determine the domain name
            string domain = account.Account.DomainName;

            // Could already be registered if there are multiple accounts on the same domain
            GABHandler gab;
            if (!_gabsByDomainName.TryGetValue(domain, out gab))
            {
                // Create the handler
                gab = new GABHandler(this, (f) => CreateGABContacts(domain), (f) => DisposeGABContacts(f));
                _gabsByDomainName.Add(domain, gab);
            }
            else
            {
                Logger.Instance.Debug(this, "GAB handler already registered: {0} on {1}", folder, folder.StoreDisplayName);
            }

            // Register the account with the GAB
            gab.AddAccount(account, folder);

            // The folder has become available, check the GAB messages
            DoProcess(account, gab, null);

            // And watch for any new messages
            Watcher.WatchItems<IZPushItem>(folder, (item) => DoProcess(account, gab, item), false);
        }

        #endregion

        #region Processing

        private void ZPushChannelAvailable(IFolder folder)
        {
            Logger.Instance.Debug(this, "Z-Push channel available 0: {0}", folder);
            using (IStore store = folder.GetStore())
            {
                Logger.Instance.Debug(this, "Z-Push channel available: {0} on {1}", folder, store.DisplayName);

                ZPushAccount account = Watcher.Accounts.GetAccount(folder);
                if (account != null)
                {
                    Logger.Instance.Debug(this, "Z-Push channel available 0: {0} -> {1}", folder, account);
                    account.LinkedGABFolder(folder);
                    RegisterGABAccount(account, folder);
                }
                else
                {
                    Logger.Instance.Warning(this, "Z-Push channel account not found: {0} on {1}", folder, store.DisplayName);
                }
                Logger.Instance.Debug(this, "Z-Push channel available done");
            }
        }

        private void DoProcess(ZPushAccount account, GABHandler gab, IZPushItem item)
        {
            // Multiple accounts - and therefore multiple folders - may use the same GAB.
            // One process the items from the first associated account
            if (account != gab.ActiveAccount)
            {
                Logger.Instance.Trace(this, "Ignoring GAB message: {0} - {1}", account, item);
                return;
            }

            ++_processing;
            Logger.Instance.Trace(this, "Processing GAB message: {0} - {1}", account, _processing);
            CompletionTracker completion = new CompletionTracker(() => OnGabSyncFinished(gab));
            using (completion.Begin())
            {
                try
                {
                    gab.Process(completion, item);
                    // TODO: this will probably run while still processing, use completion tracker
                    DoEmptyDeletedItems();
                }
                finally
                {
                    Logger.Instance.Trace(this, "Processed GAB message: {0} - {1}", account, _processing);
                    --_processing;
                }
            }
        }

        private void DoEmptyDeletedItems()
        {
            if (!EmptyDeletedItems)
                return;

            if (_store != null)
                _store.EmptyDeletedItems();
        }

        #endregion
    }
}