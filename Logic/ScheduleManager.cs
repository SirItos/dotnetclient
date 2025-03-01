/**
 * Copyright (C) 2022 Xibo Signage Ltd
 *
 * Xibo - Digital Signage - http://www.xibo.org.uk
 *
 * This file is part of Xibo.
 *
 * Xibo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * any later version.
 *
 * Xibo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with Xibo.  If not, see <http://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using XiboClient.Action;
using XiboClient.Adspace;
using XiboClient.Helpers;
using XiboClient.Log;
using XiboClient.Logic;

namespace XiboClient
{
    /// <summary>
    /// Schedule manager controls the currently running schedule
    /// </summary>
    class ScheduleManager
    {
        #region "Constructor"

        // Thread Logic
        public static object _locker = new object();
        private bool _forceStop = false;
        private ManualResetEvent _manualReset = new ManualResetEvent(false);

        // Event for new schedule
        public delegate void OnNewScheduleAvailableDelegate();
        public event OnNewScheduleAvailableDelegate OnNewScheduleAvailable;

        public delegate void OnRefreshScheduleDelegate();
        public event OnRefreshScheduleDelegate OnRefreshSchedule;

        // Event for Subscriber inactive
        public delegate void OnScheduleManagerCheckCompleteDelegate();
        public event OnScheduleManagerCheckCompleteDelegate OnScheduleManagerCheckComplete;

        // Member Varialbes
        private string _location;
        private List<LayoutChangePlayerAction> _layoutChangeActions;
        private List<OverlayLayoutPlayerAction> _overlayLayoutActions;
        private List<ScheduleItem> _layoutSchedule;
        private List<ScheduleCommand> _commands;
        private List<ScheduleItem> _overlaySchedule;
        private List<ScheduleItem> _invalidSchedule;
        private List<Action.Action> _actionsSchedule;

        // State
        private bool _refreshSchedule;
        private DateTime _lastScreenShotDate;

        // Adspace Exchange Manager
        private ExchangeManager exchangeManager;

        /// <summary>
        /// Creates a new schedule Manager
        /// </summary>
        /// <param name="scheduleLocation"></param>
        public ScheduleManager(string scheduleLocation)
        {
            _location = scheduleLocation;

            // Create an empty layout schedule
            _layoutSchedule = new List<ScheduleItem>();
            CurrentSchedule = new List<ScheduleItem>();
            _layoutChangeActions = new List<LayoutChangePlayerAction>();
            _commands = new List<ScheduleCommand>();
            CurrentDefaultLayout = ScheduleItem.Splash();

            // Overlay schedules
            CurrentOverlaySchedule = new List<ScheduleItem>();
            _overlaySchedule = new List<ScheduleItem>();
            _overlayLayoutActions = new List<OverlayLayoutPlayerAction>();

            // Action schedules
            CurrentActionsSchedule = new List<Action.Action>();
            _actionsSchedule = new List<Action.Action>();

            // Screenshot
            _lastScreenShotDate = DateTime.MinValue;

            // Create a new exchange manager
            exchangeManager = new ExchangeManager();
        }

        #endregion

        #region "Properties"

        /// <summary>
        /// Tell the schedule manager to Refresh the Schedule
        /// </summary>
        public bool RefreshSchedule
        {
            get
            {
                return _refreshSchedule;
            }
            set
            {
                lock (_locker)
                    _refreshSchedule = value;
            }
        }

        /// <summary>
        /// The current default layout
        /// </summary>
        public ScheduleItem CurrentDefaultLayout { get; private set; }

        /// <summary>
        /// The current layout schedule
        /// </summary>
        public List<ScheduleItem> CurrentSchedule { get; private set; }

        /// <summary>
        /// Get the current overlay schedule
        /// </summary>
        public List<ScheduleItem> CurrentOverlaySchedule { get; private set; }

        /// <summary>
        /// The current scheduled actions
        /// </summary>
        public List<Action.Action> CurrentActionsSchedule { get; private set; }

        #endregion

        /// <summary>
        /// Stops the thread
        /// </summary>
        public void Stop()
        {
            _forceStop = true;
            _manualReset.Set();
        }

        /// <summary>
        /// Runs the schedule manager now
        /// </summary>
        public void RunNow()
        {
            _manualReset.Set();
        }

        /// <summary>
        /// Runs the Schedule Manager
        /// </summary>
        public void Run()
        {
            Trace.WriteLine(new LogMessage("ScheduleManager - Run", "Thread Started"), LogType.Info.ToString());

            // Create a GeoCoordinateWatcher
            GeoCoordinateWatcher watcher = null;
            try
            {
                watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.High)
                {
                    MovementThreshold = 10
                };
                watcher.PositionChanged += Watcher_PositionChanged;
                watcher.StatusChanged += Watcher_StatusChanged;
                watcher.Start();
            }
            catch (Exception e)
            {
                Trace.WriteLine(new LogMessage("ScheduleManager", "Run: GeoCoordinateWatcher failed to start. E = " + e.Message), LogType.Error.ToString());
            }

            // Run loop
            // --------
            while (!_forceStop)
            {
                lock (_locker)
                {
                    try
                    {
                        // If we are restarting, reset
                        _manualReset.Reset();

                        Trace.WriteLine(new LogMessage("ScheduleManager - Run", "Schedule Timer Ticked"), LogType.Audit.ToString());

                        // Work out if there is a new schedule available, if so - raise the event
                        // Events
                        // ------
                        if (IsNewScheduleAvailable())
                        {
                            OnNewScheduleAvailable();
                        }
                        else
                        {
                            OnRefreshSchedule();
                        }

                        // Update the client info form
                        ClientInfo.Instance.ScheduleManagerStatus = LayoutsInSchedule();

                        // Do we need to take a screenshot?
                        if (ApplicationSettings.Default.ScreenShotRequestInterval > 0 && DateTime.Now > _lastScreenShotDate.AddMinutes(ApplicationSettings.Default.ScreenShotRequestInterval))
                        {
                            // Take a screen shot and send it
                            ScreenShot.TakeAndSend();

                            // Store the date
                            _lastScreenShotDate = DateTime.Now;

                            // Notify status to XMDS
                            ClientInfo.Instance.NotifyStatusToXmds();
                        }

                        // Run any commands that occur in the next 10 seconds.
                        DateTime now = DateTime.Now;
                        DateTime tenSecondsTime = now.AddSeconds(10);

                        foreach (ScheduleCommand command in _commands)
                        {
                            if (command.Date >= now && command.Date < tenSecondsTime && !command.HasRun)
                            {
                                try
                                {
                                    // We need to run this command
                                    new Thread(new ThreadStart(command.Run)).Start();

                                    // Mark run
                                    command.HasRun = true;
                                }
                                catch (Exception e)
                                {
                                    Trace.WriteLine(new LogMessage("ScheduleManager - Run", "Cannot start Thread to Run Command: " + e.Message), LogType.Error.ToString());
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log this message, but dont abort the thread
                        Trace.WriteLine(new LogMessage("ScheduleManager - Run", "Exception in Run: " + ex.Message), LogType.Error.ToString());
                        ClientInfo.Instance.ScheduleStatus = "Error. " + ex.Message;
                    }
                }

                // Completed this check
                OnScheduleManagerCheckComplete?.Invoke();

                // Sleep this thread for 10 seconds
                _manualReset.WaitOne(10 * 1000);
            }

            // Stop the watcher
            if (watcher != null)
            {
                watcher.PositionChanged -= Watcher_PositionChanged;
                watcher.StatusChanged -= Watcher_StatusChanged;
                watcher.Stop();
                watcher.Dispose();
            }

            Trace.WriteLine(new LogMessage("ScheduleManager - Run", "Thread Stopped"), LogType.Info.ToString());
        }

        #region Methods

        /// <summary>
        /// Watcher status changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Watcher_StatusChanged(object sender, GeoPositionStatusChangedEventArgs e)
        {
            switch (e.Status)
            {
                case GeoPositionStatus.Initializing:
                    Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_StatusChanged: Working on location fix"), LogType.Info.ToString());
                    break;

                case GeoPositionStatus.Ready:
                    Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_StatusChanged: Have location"), LogType.Info.ToString());
                    break;

                case GeoPositionStatus.NoData:
                    Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_StatusChanged: No data"), LogType.Info.ToString());
                    break;

                case GeoPositionStatus.Disabled:
                    Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_StatusChanged: Disabled"), LogType.Info.ToString());
                    // Restart
                    try
                    {
                        ((GeoCoordinateWatcher)sender).Start();
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_StatusChanged: Disabled and can't restart, e = " + ex.Message), LogType.Error.ToString());
                    }
                    break;
            }
        }

        /// <summary>
        /// Watcher position has changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Watcher_PositionChanged(object sender, GeoPositionChangedEventArgs<GeoCoordinate> e)
        {
            GeoCoordinate coordinate = e.Position.Location;

            if (coordinate.IsUnknown || (coordinate.Latitude == 0 && coordinate.Longitude == 0))
            {
                Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_PositionChanged: Position Unknown"), LogType.Audit.ToString());
            }
            else
            {
                // Is this more or less accurate than the one we have already?
                Trace.WriteLine(new LogMessage("ScheduleManager", "Watcher_PositionChanged: H.Accuracy = " + coordinate.HorizontalAccuracy
                    + ", V.Accuracy = " + coordinate.VerticalAccuracy
                    + ". Lat = " + coordinate.Latitude
                    + ", Long = " + coordinate.Longitude
                    + ", Course = " + coordinate.Course
                    + ", Altitude = " + coordinate.Altitude
                    + ", Speed = " + coordinate.Speed), LogType.Info.ToString());

                // Has it changed?
                if (ClientInfo.Instance.CurrentGeoLocation == null
                    || ClientInfo.Instance.CurrentGeoLocation.IsUnknown
                    || coordinate.Latitude != ClientInfo.Instance.CurrentGeoLocation.Latitude
                    || coordinate.Longitude != ClientInfo.Instance.CurrentGeoLocation.Longitude)
                {
                    // Have we moved more that 100 meters?
                    double distanceTo = 1000;
                    if (ClientInfo.Instance.CurrentGeoLocation != null && !ClientInfo.Instance.CurrentGeoLocation.IsUnknown)
                    {
                        // Grab the distance from original position
                        distanceTo = coordinate.GetDistanceTo(ClientInfo.Instance.CurrentGeoLocation);
                    }                    

                    // Take the new one.
                    ClientInfo.Instance.CurrentGeoLocation = coordinate;

                    // Wake up the schedule manager for another pass
                    if (distanceTo >= 100)
                    {
                        RefreshSchedule = true;
                    }
                }
            }
        }

        /// <summary>
        /// Determine if there is a new schedule available
        /// </summary>
        /// <returns></returns>
        private bool IsNewScheduleAvailable()
        {
            // Reassess validity
            if (_invalidSchedule == null)
            {
                _invalidSchedule = new List<ScheduleItem>();
            }
            else
            {
                _invalidSchedule.Clear();
            }

            // Remove completed change actions
            removeLayoutChangeActionIfComplete();

            // Remove completed overlay actions
            removeOverlayLayoutActionIfComplete();

            // If we dont currently have a cached schedule load one from the scheduleLocation
            // also do this if we have been told to Refresh the schedule
            if (_layoutSchedule.Count == 0 || RefreshSchedule)
            {
                // Try to load the schedule from disk
                try
                {
                    // Empty the current schedule collection
                    _layoutSchedule.Clear();

                    // Clear the list of commands
                    _commands.Clear();

                    // Clear the list of overlays
                    _overlaySchedule.Clear();

                    // Clear the list of actions
                    _actionsSchedule.Clear();

                    // Load in the schedule
                    LoadScheduleFromFile();

                    // Load in the layout change actions
                    LoadScheduleFromLayoutChangeActions();

                    // Load in the overlay actions
                    LoadScheduleFromOverlayLayoutActions();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(new LogMessage("IsNewScheduleAvailable", string.Format("Unable to load schedule from disk: {0}", ex.Message)),
                        LogType.Error.ToString());

                    // If we cant load the schedule from disk then use an empty schedule.
                    SetEmptySchedule();
                }

                // Set RefreshSchedule to be false (this means we will not need to load the file constantly)
                RefreshSchedule = false;
            }

            // Load the new Schedule
            List<ScheduleItem> parsedSchedule = ParseScheduleAndValidate();

            // Load a new overlay schedule
            List<ScheduleItem> overlaySchedule = LoadNewOverlaySchedule();

            // Load any adspace exchange schedules
            if (ApplicationSettings.Default.IsAdspaceEnabled)
            {
                exchangeManager.SetActive(true);
                exchangeManager.Configure();
                if (exchangeManager.ShareOfVoice > 0)
                {
                    parsedSchedule.Add(ScheduleItem.CreateForAdspaceExchange(exchangeManager.AverageAdDuration, exchangeManager.ShareOfVoice));
                }
            }
            else
            {
                exchangeManager.SetActive(false);
            }

            // Do we have any change layout actions?
            List<ScheduleItem> newSchedule = GetOverrideSchedule(parsedSchedule);
            if (newSchedule.Count <= 0)
            {
                // No overrides, so we parse in our normal/interrupt layout mix.
                newSchedule = ResolveNormalAndInterrupts(ParseCyclePlayback(parsedSchedule));
            }

            // If we have come out of this process without any schedule, then we ought to assign the default
            if (newSchedule.Count <= 0)
            {
                newSchedule = new List<ScheduleItem>()
                {
                    CurrentDefaultLayout
                };
            }

            // Should we force a change 
            // (broadly this depends on whether or not the schedule has changed.)
            bool forceChange = false;

            // If the current schedule is empty, always overwrite
            if (CurrentSchedule.Count == 0)
            {
                forceChange = true;
            }

            // Are all the items that were in the _currentSchedule still there?
            foreach (ScheduleItem layout in CurrentSchedule)
            {
                if (!newSchedule.Contains(layout))
                {
                    Trace.WriteLine(new LogMessage("ScheduleManager - IsNewScheduleAvailable", "New Schedule does not contain " + layout.id), LogType.Audit.ToString());
                    forceChange = true;
                }
            }

            // Try to work out whether the overlay schedule has changed or not.
            // easiest way to do this is to see if the sizes have changed
            if (CurrentOverlaySchedule.Count != overlaySchedule.Count)
            {
                forceChange = true;
            }
            else
            {
                // Compare them on an object by object level.
                // Are all the items that were in the _currentOverlaySchedule still there?
                foreach (ScheduleItem layout in CurrentOverlaySchedule)
                {
                    // New overlay schedule doesn't contain the layout?
                    if (!overlaySchedule.Contains(layout))
                    {
                        forceChange = true;
                    }
                }
            }

            // Finalise
            // --------
            // Set the new schedule
            CurrentSchedule = newSchedule;

            // Set the new Overlay schedule
            CurrentOverlaySchedule = overlaySchedule;

            // Set the Actions schedule
            CurrentActionsSchedule = _actionsSchedule;

            // Return True if we want to refresh the schedule OR false if we are OK to leave the current one.
            // We can update the current schedule and still return false - this will not trigger a schedule change event.
            // We do this if ALL the current layouts are still in the schedule
            return forceChange;
        }

        /// <summary>
        /// Loads a new schedule from _layoutSchedules
        /// </summary>
        /// <returns></returns>
        private List<ScheduleItem> ParseScheduleAndValidate()
        {
            // We need to build the current schedule from the layout schedule (obeying date/time)
            List<ScheduleItem> resolvedSchedule = new List<ScheduleItem>();

            // Temporary default Layout incase we have no layout nodes.
            ScheduleItem defaultLayout = new ScheduleItem();

            // Store the valid layout id's
            List<int> validLayoutIds = new List<int>();
            List<int> invalidLayouts = new List<int>();

            // For each layout in the schedule determine if it is currently inside the _currentSchedule, and whether it should be
            foreach (ScheduleItem layout in _layoutSchedule)
            {
                // Is this already invalid
                if (invalidLayouts.Contains(layout.id))
                {
                    continue;
                }

                // If we haven't already assessed this layout before, then check that it is valid
                if (!validLayoutIds.Contains(layout.id))
                {
                    if (!ApplicationSettings.Default.ExpireModifiedLayouts && layout.id == ClientInfo.Instance.CurrentLayoutId)
                    {
                        Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewSchedule", "Skipping validity test for current layout."), LogType.Audit.ToString());
                    }
                    else
                    {
                        // Is the layout valid in the cachemanager?
                        try
                        {
                            if (!CacheManager.Instance.IsValidPath(layout.id + ".xlf") || CacheManager.Instance.IsUnsafeLayout(layout.id))
                            {
                                invalidLayouts.Add(layout.id);
                                _invalidSchedule.Add(layout);
                                Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewSchedule", "Layout invalid: " + layout.id), LogType.Info.ToString());
                                continue;
                            }
                        }
                        catch
                        {
                            // Ignore this layout.. raise an error?
                            invalidLayouts.Add(layout.id);
                            _invalidSchedule.Add(layout);
                            Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewSchedule", "Unable to determine if layout is valid or not"), LogType.Error.ToString());
                            continue;
                        }

                        // Check dependents
                        bool validDependents = true;
                        foreach (string dependent in layout.Dependents)
                        {
                            if (!string.IsNullOrEmpty(dependent) && !CacheManager.Instance.IsValidPath(dependent))
                            {
                                invalidLayouts.Add(layout.id);
                                _invalidSchedule.Add(layout);
                                Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewSchedule", "Layout has invalid dependent: " + dependent), LogType.Info.ToString());

                                validDependents = false;
                                break;
                            }
                        }

                        if (!validDependents)
                            continue;
                    }
                }

                // Add to the valid layout ids
                validLayoutIds.Add(layout.id);

                // If this is the default, skip it
                if (layout.NodeName == "default")
                {
                    // Store it before skipping it
                    defaultLayout = layout;
                    continue;
                }

                // Look at the Date/Time to see if it should be on the schedule or not
                if (layout.FromDt <= DateTime.Now && layout.ToDt >= DateTime.Now)
                {
                    // Is it GeoAware?
                    if (layout.IsGeoAware)
                    {
                        // Check that it is inside the current location.
                        if (!layout.SetIsGeoActive(ClientInfo.Instance.CurrentGeoLocation))
                        {
                            continue;
                        }
                    }

                    resolvedSchedule.Add(layout);
                }
            }

            // Persist our new default.
            CurrentDefaultLayout = defaultLayout;

            return resolvedSchedule;
        }

        /// <summary>
        /// Parse cycle playback out of a schedule
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> ParseCyclePlayback(List<ScheduleItem> schedule)
        {
            Dictionary<string, List<ScheduleItem>> resolved = new Dictionary<string, List<ScheduleItem>>();
            resolved.Add("flat", new List<ScheduleItem>());
            foreach (ScheduleItem item in schedule)
            {
                // Clear any existing cycles
                item.CycleScheduleItems.Clear();

                // Is this item cycle playback enabled?
                if (item.IsCyclePlayback)
                {
                    if (!resolved.ContainsKey(item.CycleGroupKey))
                    {
                        // First time we've seen this group key, so add it to the flat list to mark its position.
                        resolved["flat"].Add(item);

                        // Add a new empty list
                        resolved.Add(item.CycleGroupKey, new List<ScheduleItem>());
                    }

                    resolved[item.CycleGroupKey].Add(item);
                }
                else
                {
                    resolved["flat"].Add(item);
                }
            }

            // Now we go through again and add in
            foreach (ScheduleItem item in resolved["flat"])
            {
                if (item.IsCyclePlayback)
                {
                    // Pull the relevant list and join in.
                    // We add an empty one first so that we can use the main item as sequence 0.
                    item.CycleScheduleItems.Add(new ScheduleItem());
                    item.CycleScheduleItems.AddRange(resolved[item.CycleGroupKey]);
                }
            }

            return resolved["flat"];
        }

        /// <summary>
        /// Get Normal Schedule
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> GetNormalSchedule(List<ScheduleItem> schedule)
        {
            return GetHighestPriority(schedule.FindAll(i => i.IsInterrupt() == false));
        }

        /// <summary>
        /// Get Interrupt Schedule
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> GetInterruptSchedule(List<ScheduleItem> schedule)
        {
            return GetHighestPriority(schedule.FindAll(i => i.IsInterrupt()));
        }

        /// <summary>
        /// Get Override Schedule
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> GetOverrideSchedule(List<ScheduleItem> schedule)
        {
            return schedule.FindAll(i => i.Override);
        }

        /// <summary>
        /// Get the highest priority schedule from a list of schedules.
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> GetHighestPriority(List<ScheduleItem> schedule)
        {
            int highestPriority = 0;
            List<ScheduleItem> resolved = new List<ScheduleItem>();
            foreach (ScheduleItem item in schedule)
            {
                if (item.Priority > highestPriority)
                {
                    resolved.Clear();
                    highestPriority = item.Priority;
                }

                if (item.Priority == highestPriority)
                {
                    resolved.Add(item);
                }
            }

            return resolved;
        }

        /// <summary>
        /// Resolve normal and interrupts from a parsed valid schedule
        /// </summary>
        /// <param name="schedule"></param>
        /// <returns></returns>
        private List<ScheduleItem> ResolveNormalAndInterrupts(List<ScheduleItem> schedule)
        {
            // Clear any currently set durations
            foreach (ScheduleItem item in schedule)
            {
                item.ResetCommittedDuration();
            }

            // Get the two schedules
            List<ScheduleItem> normal = GetNormalSchedule(schedule);
            List<ScheduleItem> interrupt = GetInterruptSchedule(schedule);

            if (interrupt.Count <= 0)
            {
                return normal;
            }

            // If we have an empty normal schedule, pop the default in there
            if (normal.Count <= 0)
            {
                normal = new List<ScheduleItem>
                {
                    CurrentDefaultLayout
                };
            }

            // We do have interrupts
            // organise the schedule loop so that our interrupts play according to their share of voice requirements.
            List<ScheduleItem> resolved = new List<ScheduleItem>();
            List<ScheduleItem> resolvedNormal = new List<ScheduleItem>();
            List<ScheduleItem> resolvedInterrupt = new List<ScheduleItem>();

            // Make a list of interrupt layouts which contain an instance of the event for each time that interrupt
            // needs to play to fulfil its share of voice.
            int index = 0;
            int interruptSecondsInHour = 0;

            while (true)
            {
                if (index >= interrupt.Count)
                {
                    // Start from the beginning
                    index = 0;

                    bool allSatisfied = true;
                    foreach (ScheduleItem check in interrupt)
                    {
                        if (!check.IsDurationSatisfied())
                        {
                            allSatisfied = false;
                            break;
                        }
                    }

                    // We break out when all items are satisfied.
                    if (allSatisfied)
                    {
                        break;
                    }
                }

                ScheduleItem item = interrupt[index];
                if (!item.IsDurationSatisfied())
                {
                    // The duration of this layout.
                    // from 2.3.10 CMS this is provided in XMDS
                    // if not provided, we use the last actual duration of the layout
                    int duration = (item.Duration <= 0) 
                        ? CacheManager.Instance.GetLayoutDuration(item.id, 60) 
                        : item.Duration;

                    item.AddCommittedDuration(duration);
                    interruptSecondsInHour += duration;
                    resolvedInterrupt.Add(item);
                }

                index++;
            }

            // We will have some time remaining, so go through the normal layouts and produce a schedule
            // to consume this remaining time
            int normalSecondsInHour = 3600 - interruptSecondsInHour;
            index = 0;

            while (normalSecondsInHour > 0)
            {
                if (index >= normal.Count)
                {
                    index = 0;
                }

                ScheduleItem item = normal[index];
                int duration = (item.Duration <= 0) 
                    ? CacheManager.Instance.GetLayoutDuration(item.id, 60) 
                    : item.Duration;

                // Protect against 0 durations
                if (duration <= 0)
                {
                    duration = 10;
                }

                normalSecondsInHour -= duration;
                resolvedNormal.Add(item);

                index++;
            }

            // Now we combine both schedules together, spreading the interrupts evenly
            int pickCount = Math.Max(resolvedNormal.Count, resolvedInterrupt.Count);

            // Take the ceiling of normal and the floor of interrupt
            int normalPick = (int)Math.Ceiling(1.0 * pickCount / resolvedNormal.Count);
            int interruptPick = (int)Math.Floor(1.0 * pickCount / resolvedInterrupt.Count);
            int normalIndex = 0;
            int interruptIndex = 0;

            // Pick as many times as we need to consume the larger list
            for (int i = 0; i < pickCount; i++)
            {
                // We can overpick from the normal list
                if (i % normalPick == 0)
                {
                    if (normalIndex >= resolvedNormal.Count)
                    {
                        normalIndex = 0;
                    }
                    resolved.Add(resolvedNormal[normalIndex]);
                    normalIndex++;
                }

                // We can't overpick from the interrupt list
                if (i % interruptPick == 0 && interruptIndex < resolvedInterrupt.Count)
                {
                    resolved.Add(resolvedInterrupt[interruptIndex]);
                    interruptIndex++;
                }
            }

            return resolved;
        }

        /// <summary>
        /// Loads a new schedule from _overlaySchedules
        /// </summary>
        /// <returns></returns>
        private List<ScheduleItem> LoadNewOverlaySchedule()
        {
            // We need to build the current schedule from the layout schedule (obeying date/time)
            List<ScheduleItem> newSchedule = new List<ScheduleItem>();
            List<ScheduleItem> prioritySchedule = new List<ScheduleItem>();
            List<ScheduleItem> overlayActionSchedule = new List<ScheduleItem>();

            // Store the valid layout id's
            List<int> validLayoutIds = new List<int>();
            List<int> invalidLayouts = new List<int>();

            // Store the highest priority
            int highestPriority = 1;

            // For each layout in the schedule determine if it is currently inside the _currentSchedule, and whether it should be
            foreach (ScheduleItem layout in _overlaySchedule)
            {
                // Set to overlay
                layout.IsOverlay = true;

                // Is this already invalid
                if (invalidLayouts.Contains(layout.id))
                    continue;

                // If we haven't already assessed this layout before, then check that it is valid
                if (!validLayoutIds.Contains(layout.id))
                {
                    // Is the layout valid in the cachemanager?
                    try
                    {
                        if (!CacheManager.Instance.IsValidPath(layout.id + ".xlf") || CacheManager.Instance.IsUnsafeLayout(layout.id))
                        {
                            invalidLayouts.Add(layout.id);
                            _invalidSchedule.Add(layout);
                            Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewOverlaySchedule", "Layout invalid: " + layout.id), LogType.Info.ToString());
                            continue;
                        }
                    }
                    catch
                    {
                        // Ignore this layout.. raise an error?
                        invalidLayouts.Add(layout.id);
                        _invalidSchedule.Add(layout);
                        Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewOverlaySchedule", "Unable to determine if layout is valid or not"), LogType.Error.ToString());
                        continue;
                    }

                    // Check dependents
                    foreach (string dependent in layout.Dependents)
                    {
                        if (!CacheManager.Instance.IsValidPath(dependent))
                        {
                            invalidLayouts.Add(layout.id);
                            _invalidSchedule.Add(layout);
                            Trace.WriteLine(new LogMessage("ScheduleManager - LoadNewOverlaySchedule", "Layout has invalid dependent: " + dependent), LogType.Info.ToString());
                            continue;
                        }
                    }
                }

                // Add to the valid layout ids
                validLayoutIds.Add(layout.id);

                // Look at the Date/Time to see if it should be on the schedule or not
                if (layout.FromDt <= DateTime.Now && layout.ToDt >= DateTime.Now)
                {
                    // Change Action and Priority layouts should generate their own list
                    if (layout.Override)
                    {
                        overlayActionSchedule.Add(layout);
                    }
                    else if (layout.Priority >= 1)
                    {
                        // Is this higher than our priority already?
                        if (layout.Priority > highestPriority)
                        {
                            prioritySchedule.Clear();
                            prioritySchedule.Add(layout);

                            // Store the new highest priority
                            highestPriority = layout.Priority;
                        }
                        else if (layout.Priority == highestPriority)
                        {
                            prioritySchedule.Add(layout);
                        }
                    }
                    else
                    {
                        newSchedule.Add(layout);
                    }
                }
            }

            // Have we got any overlay actions
            if (overlayActionSchedule.Count > 0)
                return overlayActionSchedule;

            // If we have any priority schedules then we need to return those instead
            if (prioritySchedule.Count > 0)
                return prioritySchedule;

            return newSchedule;
        }

        /// <summary>
        /// Loads the schedule from file.
        /// </summary>
        /// <returns></returns>
        private void LoadScheduleFromFile()
        {
            // Get the schedule XML
            XmlDocument scheduleXml = GetScheduleXml();

            // Parse the schedule xml
            XmlNodeList nodes = scheduleXml["schedule"].ChildNodes;

            // Are there any nodes in the document
            if (nodes.Count == 0)
            {
                SetEmptySchedule();
                return;
            }

            // We have nodes, go through each one and add them to the layoutschedule collection
            foreach (XmlNode node in nodes)
            {
                // Node name
                if (node.Name == "dependants")
                {
                    // Do nothing for now
                }
                else if (node.Name == "command")
                {
                    // Try to get the command using the code
                    try
                    {
                        // Pull attributes from layout nodes
                        XmlAttributeCollection attributes = node.Attributes;

                        ScheduleCommand command = new ScheduleCommand();
                        command.Date = DateTime.Parse(attributes["date"].Value, CultureInfo.InvariantCulture);
                        command.Code = attributes["code"].Value;
                        command.ScheduleId = int.Parse(attributes["scheduleid"].Value);

                        // Add to the collection
                        _commands.Add(command);
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine(new LogMessage("ScheduleManager - LoadScheduleFromFile", e.Message), LogType.Error.ToString());
                    }
                }
                else if (node.Name == "overlays")
                {
                    // Parse out overlays and load them into their own schedule
                    foreach (XmlNode overlayNode in node.ChildNodes)
                    {
                        _overlaySchedule.Add(ParseNodeIntoScheduleItem(overlayNode));
                    }
                }
                else if (node.Name == "actions")
                {
                    ParseNodeListIntoActions(node.ChildNodes);
                }
                else
                {
                    _layoutSchedule.Add(ParseNodeIntoScheduleItem(node));
                }
            }

            // Clean up
            nodes = null;
            scheduleXml = null;

            // We now have the saved XML contained in the _layoutSchedule object
        }

        /// <summary>
        /// Parse an XML node from XMDS into a Schedule Item
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        private ScheduleItem ParseNodeIntoScheduleItem(XmlNode node)
        {
            ScheduleItem temp = new ScheduleItem
            {
                NodeName = node.Name
            };

            // Pull attributes from layout nodes
            XmlAttributeCollection attributes = node.Attributes;

            // All nodes have file properties
            temp.layoutFile = attributes["file"].Value;

            // Replace the .xml extension with nothing
            string replace = ".xml";
            string layoutFile = temp.layoutFile.TrimEnd(replace.ToCharArray());

            // Set these on the temp layoutschedule
            temp.layoutFile = ApplicationSettings.Default.LibraryPath + @"\" + layoutFile + @".xlf";
            temp.id = int.Parse(layoutFile);

            // Dependents
            if (attributes["dependents"] != null && !string.IsNullOrEmpty(attributes["dependents"].Value))
            {
                foreach (string dependent in attributes["dependents"].Value.Split(','))
                {
                    temp.Dependents.Add(dependent);
                }
            }

            // Get attributes that only exist on the default
            if (temp.NodeName != "default")
            {
                // Priority flag
                try
                {
                    temp.Priority = int.Parse(attributes["priority"].Value);
                }
                catch
                {
                    temp.Priority = 0;
                }

                // Get the fromdt,todt
                temp.FromDt = DateTime.Parse(attributes["fromdt"].Value, CultureInfo.InvariantCulture);
                temp.ToDt = DateTime.Parse(attributes["todt"].Value, CultureInfo.InvariantCulture);

                // Pull out the scheduleid if there is one
                string scheduleId = "";
                if (attributes["scheduleid"] != null)
                {
                    scheduleId = attributes["scheduleid"].Value;
                }

                // Add it to the layout schedule
                if (scheduleId != "")
                {
                    temp.scheduleid = int.Parse(scheduleId);
                }

                // Dependents
                if (attributes["dependents"] != null)
                {
                    foreach (string dependent in attributes["dependents"].Value.Split(','))
                    {
                        temp.Dependents.Add(dependent);
                    }
                }

                // Geo Schedule
                if (attributes["isGeoAware"] != null)
                {
                    temp.IsGeoAware = (attributes["isGeoAware"].Value == "1");
                    temp.GeoLocation = attributes["geoLocation"] != null ? attributes["geoLocation"].Value : "";
                }

                // Share of Voice
                if (attributes["shareOfVoice"] != null)
                {
                    try
                    {
                        temp.ShareOfVoice = int.Parse(attributes["shareOfVoice"].Value);
                    }
                    catch
                    {
                        temp.ShareOfVoice = 0;
                    }
                }

                // Duration
                if (attributes["duration"] != null)
                {
                    try
                    {
                        temp.Duration = int.Parse(attributes["duration"].Value);
                    }
                    catch
                    {
                        temp.Duration = 0;
                    }
                }

                // Cycle playback
                try
                {
                    temp.IsCyclePlayback = int.Parse(XmlHelper.GetAttrib(node, "cyclePlayback", "0")) == 1;
                    temp.CycleGroupKey = XmlHelper.GetAttrib(node, "groupKey", "");
                    temp.CyclePlayCount = int.Parse(XmlHelper.GetAttrib(node, "playCount", "1"));
                }
                catch
                {
                    Trace.WriteLine(new LogMessage("ScheduleManager", "ParseNodeIntoScheduleItem: invalid cycle playback configuration."), LogType.Audit.ToString());
                }
            }

            // Look for dependents nodes
            foreach (XmlNode childNode in node.ChildNodes)
            {
                if (childNode.Name == "dependents")
                {
                    foreach (XmlNode dependent in childNode.ChildNodes)
                    {
                        if (dependent.Name == "file")
                        {
                            temp.Dependents.Add(dependent.InnerText);
                        }
                    }
                }
            }

            return temp;
        }

        /// <summary>
        /// Parse a node list of actions into actual actions
        /// </summary>
        /// <param name="nodes"></param>
        private void ParseNodeListIntoActions(XmlNodeList nodes)
        {
            // Track the highest priority
            int highestPriority = 0;

            foreach (XmlNode node in nodes)
            {
                XmlAttributeCollection attributes = node.Attributes;

                // Priority flag
                int actionPriority;
                try
                {
                    actionPriority = int.Parse(attributes["priority"].Value);
                }
                catch
                {
                    actionPriority = 0;
                }

                // Get the fromdt,todt
                DateTime fromDt = DateTime.Parse(attributes["fromdt"].Value, CultureInfo.InvariantCulture);
                DateTime toDt = DateTime.Parse(attributes["todt"].Value, CultureInfo.InvariantCulture);

                if (DateTime.Now > fromDt && DateTime.Now < toDt)
                {
                    // Geo Schedule
                    if (attributes["isGeoAware"] != null && attributes["isGeoAware"].Value == "1")
                    {
                        // Test the geo location and skip if we're outside
                        string geoLocation = attributes["geoLocation"] != null ? attributes["geoLocation"].Value : "";
                        if (string.IsNullOrEmpty(geoLocation)
                            || ClientInfo.Instance.CurrentGeoLocation == null
                            || ClientInfo.Instance.CurrentGeoLocation.IsUnknown)
                        {
                            continue;
                        }

                        // Test the geolocation
                        if (!GeoHelper.IsGeoInPoint(geoLocation, ClientInfo.Instance.CurrentGeoLocation))
                        {
                            continue;
                        }
                    }

                    // is this a new high watermark for priority
                    if (actionPriority > highestPriority)
                    {
                        _actionsSchedule.Clear();
                        highestPriority = actionPriority;
                    }

                    _actionsSchedule.Add(Action.Action.CreateFromScheduleNode(node));
                }
            }
        }

        /// <summary>
        /// Load schedule from layout change actions
        /// </summary>
        private void LoadScheduleFromLayoutChangeActions()
        {
            if (_layoutChangeActions.Count <= 0)
                return;

            // Loop through the layout change actions and create schedule items for them
            foreach (LayoutChangePlayerAction action in _layoutChangeActions)
            {
                if (action.downloadRequired)
                    continue;

                DateTime actionCreateDt = DateTime.Parse(action.createdDt);

                ScheduleItem item = new ScheduleItem();
                item.FromDt = actionCreateDt.AddSeconds(-1);
                item.ToDt = DateTime.MaxValue;
                item.id = action.layoutId;
                item.scheduleid = 0;
                item.actionId = action.GetId();
                item.Priority = 0;
                item.Override = true;
                item.NodeName = "layout";
                item.layoutFile = ApplicationSettings.Default.LibraryPath + @"\" + item.id + @".xlf";

                _layoutSchedule.Add(item);
            }
        }

        /// <summary>
        /// Load schedule from layout change actions
        /// </summary>
        private void LoadScheduleFromOverlayLayoutActions()
        {
            if (_overlayLayoutActions.Count <= 0)
                return;

            // Loop through the layout change actions and create schedule items for them
            foreach (OverlayLayoutPlayerAction action in _overlayLayoutActions)
            {
                removeOverlayLayoutActionIfComplete();

                if (action.downloadRequired)
                    continue;

                ScheduleItem item = new ScheduleItem();
                item.FromDt = DateTime.MinValue;
                item.ToDt = DateTime.MaxValue;
                item.id = action.layoutId;
                item.scheduleid = action.layoutId;
                item.actionId = action.GetId();
                item.Priority = 0;
                item.Override = true;
                item.NodeName = "layout";
                item.layoutFile = ApplicationSettings.Default.LibraryPath + @"\" + item.id + @".xlf";

                _overlaySchedule.Add(item);
            }
        }

        /// <summary>
        /// Sets an empty schedule into the _layoutSchedule Collection
        /// </summary>
        private void SetEmptySchedule()
        {
            Debug.WriteLine("Setting an empty schedule", LogType.Info.ToString());

            // Remove the existing schedule
            _layoutSchedule.Clear();

            // Add the splash
            _layoutSchedule.Add(ScheduleItem.Splash());
        }

        /// <summary>
        /// Gets the Schedule XML
        /// </summary>
        /// <returns></returns>
        private XmlDocument GetScheduleXml()
        {
            Debug.WriteLine("Getting the Schedule XML", LogType.Info.ToString());

            XmlDocument scheduleXml;

            // Check the schedule file exists
            if (File.Exists(_location))
            {
                // Read the schedule file
                XmlReader reader = XmlReader.Create(_location);

                scheduleXml = new XmlDocument();
                scheduleXml.Load(reader);

                reader.Close();
            }
            else
            {
                // Use the default XML
                scheduleXml = new XmlDocument();
                scheduleXml.LoadXml("<schedule></schedule>");
            }

            return scheduleXml;
        }

        /// <summary>
        /// Get the schedule XML from Disk into a string
        /// </summary>
        /// <param name="scheduleLocation"></param>
        /// <returns></returns>
        public static string GetScheduleXmlString(string scheduleLocation)
        {
            lock (_locker)
            {
                Trace.WriteLine(new LogMessage("ScheduleManager - GetScheduleXmlString", "Getting the Schedule XML"), LogType.Audit.ToString());

                string scheduleXml;

                // Check the schedule file exists
                try
                {
                    // Read the schedule file
                    using (FileStream fileStream = File.Open(scheduleLocation, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        using (StreamReader sr = new StreamReader(fileStream))
                        {
                            scheduleXml = sr.ReadToEnd();
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    // Use the default XML
                    scheduleXml = "<schedule></schedule>";
                }

                return scheduleXml;
            }
        }

        /// <summary>
        /// Write the Schedule XML to disk from a String
        /// </summary>
        /// <param name="scheduleLocation"></param>
        /// <param name="scheduleXml"></param>
        public static void WriteScheduleXmlToDisk(string scheduleLocation, string scheduleXml)
        {
            lock (_locker)
            {
                using (StreamWriter sw = new StreamWriter(scheduleLocation, false, Encoding.UTF8))
                {
                    sw.Write(scheduleXml);
                }
            }
        }

        /// <summary>
        /// List of Layouts in the Schedule
        /// </summary>
        /// <returns></returns>
        private string LayoutsInSchedule()
        {
            string layoutsInSchedule = "";

            foreach (ScheduleItem layoutSchedule in CurrentSchedule)
            {
                if (layoutSchedule.Override)
                {
                    layoutsInSchedule += "API Action ";
                }

                layoutsInSchedule += "Normal: " + layoutSchedule.ToString() + Environment.NewLine;
            }

            foreach (ScheduleItem layoutSchedule in CurrentOverlaySchedule)
            {
                layoutsInSchedule += "Overlay: " + layoutSchedule.ToString() + Environment.NewLine;
            }

            foreach (ScheduleItem layoutSchedule in _invalidSchedule)
            {
                layoutsInSchedule += "Invalid: " + layoutSchedule.ToString() + Environment.NewLine;
            }

            //Debug.WriteLine("LayoutsInSchedule: " + layoutsInSchedule, "ScheduleManager");

            return layoutsInSchedule;
        }

        /// <summary>
        /// Add a layout change action
        /// </summary>
        /// <param name="action"></param>
        public void AddLayoutChangeAction(LayoutChangePlayerAction action)
        {
            _layoutChangeActions.Add(action);
            RefreshSchedule = true;
        }

        /// <summary>
        /// Replace Layout Change Action
        /// </summary>
        /// <param name="action"></param>
        public void ReplaceLayoutChangeActions(LayoutChangePlayerAction action)
        {
            ClearLayoutChangeActions();
            AddLayoutChangeAction(action);
        }

        /// <summary>
        /// Clear Layout Change Actions
        /// </summary>
        public void ClearLayoutChangeActions()
        {
            _layoutChangeActions.Clear();
            RefreshSchedule = true;
        }

        /// <summary>
        /// Assess and Remove the Layout Change Action if completed
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool removeLayoutChangeActionIfComplete(ScheduleItem item)
        {
            // Check each Layout Change Action we own and compare to the current item
            foreach (LayoutChangePlayerAction action in _layoutChangeActions)
            {
                if (item.id == action.layoutId && item.actionId == action.GetId())
                {
                    // we've played
                    action.SetPlayed();

                    // Does this conclude this change action?
                    if (action.IsServiced())
                    {
                        _layoutChangeActions.Remove(action);
                        RefreshSchedule = true;
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Remove Layout Change actions if they have completed
        /// </summary>
        public void removeLayoutChangeActionIfComplete()
        {
            // Check every action to see if complete
            foreach (LayoutChangePlayerAction action in _layoutChangeActions)
            {
                if (action.IsServiced())
                {
                    _layoutChangeActions.Remove(action);
                    RefreshSchedule = true;
                }
            }
        }

        /// <summary>
        /// Add an overlay layout action
        /// </summary>
        /// <param name="action"></param>
        public void AddOverlayLayoutAction(OverlayLayoutPlayerAction action)
        {
            _overlayLayoutActions.Add(action);
            RefreshSchedule = true;
        }

        /// <summary>
        /// Remove Overlay Layout Actions if they are complete
        /// </summary>
        /// <param name="item"></param>
        public void removeOverlayLayoutActionIfComplete()
        {
            // Check each Layout Change Action we own and compare to the current item
            foreach (OverlayLayoutPlayerAction action in _overlayLayoutActions)
            {
                if (action.IsServiced())
                {
                    _overlayLayoutActions.Remove(action);
                    RefreshSchedule = true;
                }
            }
        }

        /// <summary>
        /// Set all Layout Change Actions to be downloaded
        /// </summary>
        public void setAllActionsDownloaded()
        {
            foreach (LayoutChangePlayerAction action in _layoutChangeActions)
            {
                if (action.downloadRequired)
                {
                    action.downloadRequired = false;
                    RefreshSchedule = true;
                }
            }

            foreach (OverlayLayoutPlayerAction action in _overlayLayoutActions)
            {
                if (action.downloadRequired)
                {
                    action.downloadRequired = false;
                    RefreshSchedule = true;
                }
            }
        }

        /// <summary>
        /// Get an ad from the exchange
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public Ad GetAd(double width, double height)
        {
            return exchangeManager.GetAd(width, height);
        }

        #endregion
    }
}
