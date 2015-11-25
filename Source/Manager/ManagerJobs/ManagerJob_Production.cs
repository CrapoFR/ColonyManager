﻿// Manager/ManagerJob_Production.cs
// 
// Copyright Karel Kroeze, 2015.
// 
// Created 2015-11-05 22:59

using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using System.Reflection;

namespace FM
{
    public class ManagerJob_Production : ManagerJob
    {
        public static bool prioritizeManual = true;
        internal bool _createIngredientBills;
        internal bool _hasMeaningfulIngredientChoices;
        private readonly float _margin = Utilities.Margin;
        private string[] _targets;
        private WorkTypeDef _workTypeDef;
        public Bill_Production Bill;
        public BillGiverTracker BillGivers;
        public History History;
        public MainProductTracker MainProduct;
        public bool maxSkil;
        public new Trigger_Threshold Trigger;

        public override bool Completed
        {
            get { return !Trigger.State; }
        }

        public override ManagerTab Tab
        {
            get { return Manager.Get.ManagerTabs.Find( tab => tab is ManagerTab_Production ); }
        }

        public override bool IsValid
        {
            get
            {
                if ( Bill == null )
                {
                    return false;
                }
                Log.Message( Bill.ToString() );
                if ( Bill.recipe == null )
                {
                    return false;
                }
                Log.Message( Bill.recipe.ToString() );
                return true;
            }
        }

        private bool _otherRecipeAvailable;
        public List<RecipeDef> OtherRecipeDefs = new List<RecipeDef>();
        private int _timeSinceLastOtherRecipeCheck;
        private int _recacheThreshold = 1000;

        public void ForceRecache()
        {
            _timeSinceLastOtherRecipeCheck = _recacheThreshold;
        }
        
        public bool OtherRecipeAvailable()
        {
            // do we have a recently cached value?
            if ( _timeSinceLastOtherRecipeCheck < _recacheThreshold )
            {
                // if so, return that
                _timeSinceLastOtherRecipeCheck++;
                return _otherRecipeAvailable;
            }    
            
            // otherwise, get a list of recipes with our output that can be done on any of our workstations.
            List<RecipeDef> recipes = DefDatabase<RecipeDef>
                .AllDefsListForReading
                .Where( rd => rd != Bill.recipe &&
                              rd.products.Any( tc => tc.thingDef == MainProduct.ThingDef ) &&
                              rd.HasBuildingRecipeUser(true) )
                .ToList();

            Log.Message( "Recipe count: " + recipes.Count );

            // if there's anything in here, set the vars
            if ( recipes.Count > 0 )
            {
                _otherRecipeAvailable = true;
                _timeSinceLastOtherRecipeCheck = 0;
                OtherRecipeDefs = recipes;
            }
            else
            {
                _otherRecipeAvailable = false;
                _timeSinceLastOtherRecipeCheck = Find.TickManager.TicksGame;
                OtherRecipeDefs.Clear();
            }
            return _otherRecipeAvailable;
        }

        public void SetNewRecipe( RecipeDef newRecipe )
        {
            // clear currently assigned bills.
            CleanUp();

            // set the bill on this job
            Bill = newRecipe.UsesUnfinishedThing ? new Bill_ProductionWithUft( newRecipe ) : new Bill_Production( newRecipe );
            _hasMeaningfulIngredientChoices = Dialog_CreateJobsForIngredients.HasPrerequisiteChoices( newRecipe );

            // mainproduct and trigger do not change.
            BillGivers = new BillGiverTracker( this );

            // set the last cache time to something silly so it gets updated.
            _timeSinceLastOtherRecipeCheck = - 500;
        }

        public override string Label
        {
            get { return Bill.recipe.LabelCap; }
        }

        public override string[] Targets
        {
            get
            {
                if ( _targets == null )
                {
                    _targets = Bill.recipe.GetRecipeUsers().Select( td => td.LabelCap ).ToArray();
                }
                return _targets;
            }
        }

        public override WorkTypeDef WorkTypeDef
        {
            get
            {
                if ( _workTypeDef == null )
                {
                    // fetch the worktype def in the most convoluted way possible.
                    // first get some examples of worktables our bill is on.
                    List<Building_WorkTable> workTables = Bill.recipe.CurrentRecipeUsers();

                    // if none exist (yet), create a phony copy.
                    if ( workTables.Count == 0 )
                    {
                        workTables.Add(
                            ThingMaker.MakeThing( Bill.recipe.GetRecipeUsers().First() ) as Building_WorkTable );
                    }

                    // then loop through workgivers until we find one that matches.
                    foreach ( WorkTypeDef def in DefDatabase<WorkTypeDef>.AllDefsListForReading )
                    {
                        foreach ( WorkGiverDef workGiver in def.workGiversByPriority )
                        {
                            // we're only interested in the doBill scanner?
                            WorkGiver_DoBill scanner = workGiver.Worker as WorkGiver_DoBill;
                            if ( scanner == null )
                            {
                                continue;
                            }
#if DEBUG_OVERVIEW
                            Log.Message( Bill.recipe.LabelCap + " > " + workGiver.defName + " > " + workGiver.Worker.GetType().ToString() );
#endif

                            // skip workgiver if it applies only to pawns (cooks are not repairers!)
                            if ( workGiver.billGiversAllAnimals
                                 ||
                                 workGiver.billGiversAllAnimalsCorpses
                                 ||
                                 workGiver.billGiversAllHumanlikes
                                 ||
                                 workGiver.billGiversAllHumanlikesCorpses
                                 ||
                                 workGiver.billGiversAllMechanoids
                                 ||
                                 workGiver.billGiversAllMechanoidsCorpses )
                            {
                                continue;
                            }

                            // skip workgiver if it doesn't assign work to our tables
                            if ( !workTables.Any( workTable => scanner.PotentialWorkThingRequest.Accepts( workTable ) ) )
                            {
                                continue;
                            }

                            // if we got here that should hopefully mean we got a valid scanner for this table, and we now know what the worktype def is
                            _workTypeDef = scanner.def.workType;
                            return _workTypeDef;
                        }
                    }
                }
                return _workTypeDef;
            }
        }

        public override SkillDef SkillDef
        {
            get { return Bill.recipe.workSkill; }
        }

        public ManagerJob_Production()
        {
            // for scribe loading
        }

        public ManagerJob_Production( RecipeDef recipe )
        {
            Bill = recipe.UsesUnfinishedThing ? new Bill_ProductionWithUft( recipe ) : new Bill_Production( recipe );
            _hasMeaningfulIngredientChoices = Dialog_CreateJobsForIngredients.HasPrerequisiteChoices( recipe );
            MainProduct = new MainProductTracker( Bill.recipe );
            Trigger = new Trigger_Threshold( this );
            BillGivers = new BillGiverTracker( this );

            History = new History( new[] { Trigger.ThresholdFilter.Summary } );
        }

        /// <summary>
        /// Sorting of bills
        /// </summary>
        public static void GlobalWork()
        {
            // get a list of all assigned bills, their worktables, and the priority of the job they belong to.
            List<BillTablePriority> all = new List<BillTablePriority>();
            foreach ( ManagerJob_Production job in Manager.Get.JobStack.FullStack<ManagerJob_Production>() )
            {
                all.AddRange(
                    job.BillGivers.AssignedBillGiversAndBillsDictionary.Select(
                        pair => new BillTablePriority( pair.Key, pair.Value, job.Priority ) ) );
            }

            // no assigned bills, nothing to do.
            if ( all.Count == 0 )
            {
                return;
            }

            // loop through distinct worktables that have more than one bill
            foreach (
                Building_WorkTable table in
                    all.Select( v => v.table ).Distinct().Where( table => table.BillStack.Count > 1 ) )
            {
                // get all bills (assigned by us) for this table, pre-ordered.
                List<Bill> managerBills =
                    all.Where( v => v.table == table ).OrderBy( v => v.priority ).Select( v => v.bill as Bill ).ToList();

                // get all actual bills on the table (private field)
                object rawBillsOnTable;
                if (
                    !Utilities.TryGetPrivateField( table.billStack.GetType(), table.billStack, "bills",
                                                   out rawBillsOnTable ) )
                {
                    Log.Warning( "Failed to get real billstack for " + table );
                    continue;
                }

                // give it it's type back.
                List<Bill> billsOnTable = rawBillsOnTable as List<Bill>;
                if ( billsOnTable == null )
                {
                    Log.Warning( "Failed to convert real billstack for " + table );
                    continue;
                }

                // get the set difference of the two lists - these are external/manual bills
                List<Bill> manualBills = billsOnTable.Except( managerBills ).ToList();

                // create a new list of bills, by pasting the two lists together in the right order
                List<Bill> result = new List<Bill>();
                if ( prioritizeManual )
                {
                    result.AddRange( manualBills );
                    result.AddRange( managerBills );
                }
                else
                {
                    result.AddRange( managerBills );
                    result.AddRange( manualBills );
                }

                // feed it back to the table.
                if ( !Utilities.TrySetPrivateField( table.billStack.GetType(), table.billStack, "bills", result ) )
                {
                    Log.Warning( "Failed to set billstack for " + table );
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Deep.LookDeep( ref Bill, "Bill" );
            Scribe_Values.LookValue( ref _hasMeaningfulIngredientChoices, "hasMeaningFulIngredientChoices", false );
            Scribe_Values.LookValue( ref _createIngredientBills, "createIngredientBills", true );

            // bill giver tracking is going to error out in cross-map import/export, so create a new one.
            if ( Manager.LoadSaveMode == Manager.Modes.Normal )
            {
                Scribe_Deep.LookDeep( ref BillGivers, "BillGivers", this );
            }
            else
            {
                BillGivers = new BillGiverTracker( this );
            }
            Scribe_Values.LookValue( ref maxSkil, "maxSkill", false );

            // init main product, required by trigger.
            if ( MainProduct == null )
            {
                MainProduct = new MainProductTracker( Bill.recipe );
            }

            Scribe_Deep.LookDeep( ref Trigger, "Trigger", this );

            // scribe history in normal load/save only.
            if ( Manager.LoadSaveMode == Manager.Modes.Normal )
            {
                Scribe_Deep.LookDeep( ref History, "History",
                                      new object[] { new[] { Trigger.ThresholdFilter.Summary } } );
            }
        }

        /// <summary>
        ///     Try to assign / clean up assignments
        /// </summary>
        /// <returns></returns>
        public override bool TryDoJob()
        {
#if DEBUG_JOBS
            Log.Message( "Starting job for Production Manager." );
            Log.Message( "Job: " + ToString() );
#endif

            // flag to see if anything meaningful was done, if false at end, manager will also do next job.
            bool actionTaken = false;

            if ( Trigger.State )
            {
#if DEBUG_JOBS
                Log.Message( "Checking workers for presence of bills" );
#endif

                // BillGivers that we should work with.
                List<Building_WorkTable> workers = BillGivers.SelectedBillGivers;

                // clean up bills on workstations that do not meet selection criteria (area, count, etc) (anymore).
                CleanNoLongerAllowedBillgivers( workers, BillGivers.AssignedBillGiversAndBillsDictionary,
                                                ref actionTaken );

                // check if there's places we need to add the bill.
                for ( int workerIndex = 0; workerIndex < workers.Count; workerIndex++ )
                {
                    Building_WorkTable worker = workers[workerIndex];
#if DEBUG_JOBS
                    Log.Message( "Checking worker " + worker.LabelCap );
#endif
                    bool billPresent = false;

                    // loop over workstations
                    if ( worker.BillStack != null &&
                         worker.BillStack.Count > 0 )
                    {
#if DEBUG_JOBS
                        foreach (
                            KeyValuePair< Bill_Production, Building_WorkTable > pair in
                                BillGivers.AssignedBillGiversAndBillsDictionary )
                        {
                            Log.Message( "saved" + pair.Key.GetUniqueLoadID() + " | " + pair.Value.GetUniqueLoadID() );
                        }
#endif

                        // loop over billstack to see if our bill is set.
                        foreach ( Bill t in worker.BillStack )
                        {
                            Bill_Production thatBill = t as Bill_Production;
#if DEBUG_JOBS
                            if ( thatBill != null )
                            {
                                Log.Message( "real" + thatBill.GetUniqueLoadID() + " | " + worker.GetUniqueLoadID() );
                            }
#endif

                            // if there is a bill, and it's managed by us, check to see if it's up-to-date.
                            if ( thatBill != null &&
                                 thatBill.recipe == Bill.recipe &&
                                 BillGivers.AssignedBillGiversAndBillsDictionary.Contains(
                                     new KeyValuePair<Bill_Production, Building_WorkTable>( thatBill, worker ) ) )
                            {
                                billPresent = true;
                                if ( thatBill.suspended != Bill.suspended ||
                                     thatBill.repeatCount == 0 )
                                {
#if DEBUG_JOBS
                                    Log.Message( "Trying to unsuspend and/or bump targetCount" );
#endif
                                    thatBill.suspended = Bill.suspended;
                                    thatBill.repeatCount = this.CountPerWorker( workerIndex );
                                    actionTaken = true;
                                }

                                // update filters, modes, etc.
                                Update( thatBill, ref actionTaken );
                            }
                        }
                    }
#if DEBUG_JOBS
                    Log.Message( "Billstack scanned, bill was " + ( billPresent ? "" : "not " ) + "set" );
#endif

                    // if bill wasn't present, add it.
                    if ( !billPresent )
                    {
#if DEBUG_JOBS
                        Log.Message( "Trying to add bill" );
#endif
                        Bill_Production copy = Bill.Copy();
                        copy.repeatMode = BillRepeatMode.RepeatCount;
                        copy.repeatCount = this.CountPerWorker( workerIndex );
                        worker.BillStack?.AddBill( copy );
                        BillGivers.AssignedBillGiversAndBillsDictionary.Add( copy, worker );
                        actionTaken = true;
                    }
                }
            }
            else // Trigger false, clean up.
            {
                CleanUp();
            }
            return actionTaken;
        }

        /// <summary>
        ///     Delete outstanding managed jobs on billgivers that no longer meet criteria
        /// </summary>
        /// <param name="workers">Allowed workstations</param>
        /// <param name="assignedBills">Managed bills/workstations</param>
        /// <param name="actionTaken">Was anything done?</param>
        private void CleanNoLongerAllowedBillgivers( List<Building_WorkTable> workers,
                                                     Dictionary<Bill_Production, Building_WorkTable> assignedBills,
                                                     ref bool actionTaken )
        {
#if DEBUG_JOBS
            Log.Message( "Cleaning no longer allowed billgivers" );
#endif
            Dictionary<Bill_Production, Building_WorkTable> toBeDeleted =
                new Dictionary<Bill_Production, Building_WorkTable>();
            foreach (
                KeyValuePair<Bill_Production, Building_WorkTable> pair in
                    assignedBills.Where( pair => !workers.Contains( pair.Value ) ) )
            {
#if DEBUG_JOBS
                Log.Message( "Deleting " + pair.Key.LabelCap + " from " + pair.Value.LabelCap );
#endif
                pair.Value.BillStack.Delete( pair.Key );
                toBeDeleted.Add( pair.Key, pair.Value );
                actionTaken = true;
            }
            foreach ( KeyValuePair<Bill_Production, Building_WorkTable> pair in toBeDeleted )
            {
                assignedBills.Remove( pair.Key );
            }
        }

        /// <summary>
        ///     update bill settings
        /// </summary>
        /// <param name="thatBill">Managed bill</param>
        /// <param name="actionTaken">Any changes made?</param>
        private void Update( Bill_Production thatBill, ref bool actionTaken )
        {
            if ( thatBill.storeMode != Bill.storeMode )
            {
                thatBill.storeMode = Bill.storeMode;
                actionTaken = true;
            }

            if ( thatBill.ingredientFilter != Bill.ingredientFilter )
            {
                thatBill.ingredientFilter = Bill.ingredientFilter;
                actionTaken = true;
            }

            if ( Math.Abs( thatBill.ingredientSearchRadius - Bill.ingredientSearchRadius ) > 1 )
            {
                thatBill.ingredientSearchRadius = Bill.ingredientSearchRadius;
                actionTaken = true;
            }

            if ( thatBill.minSkillLevel != Bill.minSkillLevel )
            {
                thatBill.minSkillLevel = Bill.minSkillLevel;
                actionTaken = true;
            }
        }

        /// <summary>
        ///     Delete all outstanding managed bills for this job.
        /// </summary>
        public override void CleanUp()
        {
#if DEBUG_JOBS
            Log.Message( "Cleaning up obsolete bills" );
#endif      
            // remove managed bills from worktables.
            List<Bill_Production> toBeDeleted = new List<Bill_Production>();
            foreach (
                KeyValuePair<Bill_Production, Building_WorkTable> pair in
                    BillGivers.AssignedBillGiversAndBillsDictionary )
            {
                pair.Value.BillStack.Delete( pair.Key );
#if DEBUG_JOBS
                Log.Message( "Checking worker " + pair.Value.LabelCap );
#endif
            }

            BillGivers.AssignedBillGiversAndBillsDictionary.Clear();

        }

        public override string ToString()
        {
            string strout = base.ToString();
            strout += "\n" + Bill;
            return strout;
        }

        public override void DrawListEntry( Rect rect, bool overview = true, bool active = true )
        {
            // (detailButton) | name | (bar | last update)/(stamp) -> handled in Utilities.DrawStatusForListEntry
            int shownTargets = overview ? 4 : 3; // there's more space on the overview

            // set up rects
            Rect labelRect = new Rect( _margin, _margin, rect.width -
                                                         ( active ? StatusRectWidth + 4 * _margin : 2 * _margin ),
                                       rect.height - 2 * _margin ),
                 statusRect = new Rect( labelRect.xMax + _margin, _margin, StatusRectWidth, rect.height - 2 * _margin );

            // create label string
            string text = Label + "\n<i>" +
                          ( Targets.Length < shownTargets ? string.Join( ", ", Targets ) : "<multiple>" )
                          + "</i>";
            string tooltip = string.Join( ", ", Targets );

            // do the drawing
            GUI.BeginGroup( rect );

            // draw label
            Utilities.Label( labelRect, text, tooltip, TextAnchor.MiddleLeft, _margin );

            // if the bill has a manager job, give some more info.
            if ( active )
            {
                this.DrawStatusForListEntry( statusRect, Trigger );
            }
            GUI.EndGroup();
        }

        public override void DrawOverviewDetails( Rect rect )
        {
            History.DrawPlot( rect, Trigger.Count );
        }

        public override void Tick()
        {
            if ( Find.TickManager.TicksGame % 250 == 0 )
            {
                if ( maxSkil )
                {
                    Bill.minSkillLevel =
                        Find.ListerPawns.FreeColonistsSpawned.Max(
                            pawn => pawn.skills.GetSkill( Bill.recipe.workSkill ).level );
                }
            }
            History.Update( Trigger.CurCount );
        }

        public struct BillTablePriority
        {
            public Bill_Production bill;
            public Building_WorkTable table;
            public int priority;

            public BillTablePriority( Bill_Production bill, Building_WorkTable table, int priority )
            {
                this.bill = bill;
                this.table = table;
                this.priority = priority;
            }
        }
    }
}