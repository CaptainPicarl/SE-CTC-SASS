using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        #region mdk preserve
        // Picarl's CTC Setup And Stow Script (CTC SASS)
        public enum MotorStatorTypeEnum
        {
            Hinge,
            Rotor,
            AdvancedRotor,
            Mixed
        }

        // This enum describes how many which dimension you are trying to setup your CTC for. 
        // Use AzimuthAndElevation to assign a CTC both. Will assume that your Azimuth Subgrid is the 'nearest' to your host grid.
        public enum MotorStatorDimensionEnum
        {
            Azimuth,
            Elevation,
            AzimuthAndElevation,
            ElevationAndAzimuth
        }

        public MotorStatorTypeEnum MotorStatorDiscriminator = MotorStatorTypeEnum.Mixed;
        public MotorStatorDimensionEnum MotorStatorDimensions = MotorStatorDimensionEnum.AzimuthAndElevation;

        // CTC Options
        public float TargetingRange = float.MaxValue;
        public float AngleDeviation = 0.1f;

        public float StowArc = 180f;

        public float MotorStatorTorque = 28000000f;
        public float MotorStatorBrakingTorque = 28000000f;

        public float StowDisplacement = float.MaxValue;
        public float UnStowDisplacement = float.MaxValue;

        public float AzVelocityScalar = 3f;
        public float ElVelocityScalar = 3f;

        // ImpedenceDetectionThreshold: the degrees of angle used to determine if a rotor has 'stopped' or is stuck. This is the value compared against the difference between a MotorStator's last angle and current angle.
        // StopAngleTolerance: this is the 'width' of tolerance that is acceptable for a MotorStator to 'stop', since we will rarely land directly on any given angle. Determines the +/- of 'good enough' to determine we've reached an angle.
        public float ImpedenceDetectionThreshold = 0.5f;
        public float StopAngleTolerance = 1f;

        public bool RotateNearMotorStatorsOnlyDuringStow = true;

        //General Verbose Mode (Writes to CustomData)
        private bool VerboseMode = false;
        #endregion

        private WcPbApi WCAPI;

        private string CTCTag = "[PICCTC]";
        private string MotorStatorTag = "[PICMOTOR]";
        private string MergeBlockTag = "[PICMERGE]";
        private string CameraTag = "[PICCAM]";

        private List<IMyTurretControlBlock> CustomTurretControllers;

        private List<MyDefinitionId> WCStaticWeapons;
        private List<MyDefinitionId> WCTurretWeapons;
        private List<IMyTerminalBlock> WCStaticWeaponsTB;
        private List<IMyTerminalBlock> WCTurretWeaponsTB;

        // The 'near' MotorStators are the MotorStators immediately placed on the local cubegrid.
        // The 'far' MotorStators are the MotorStators place on top of the 'near' stators.
        // MotorStatorsNear are all the 'near' MotorStators
        // MotorStatorsNearFarMap are all the 'near' motorStators (key), and their associated'far' motorstator (value)
        // At the moment: This program only supports 1 near stator, and 1 far. Hence the 1:1 mapping for near:far.
        private List<IMyMotorStator> MotorStatorsNear;
        private Dictionary<IMyMotorStator, IMyMotorStator> MotorStatorsNearFarMap;

        private List<IMyShipMergeBlock> MergeBlocks;

        private Dictionary<IMyTurretControlBlock, IMyMotorStator> NearCTCToMotorMap;
        private Dictionary<IMyTurretControlBlock, IMyMotorStator> FarCTCToMotorMap;

        private Dictionary<IMyMotorStator, List<IMyLandingGear>> NearMotorToLandingGearMap;
        private Dictionary<IMyMotorStator, List<IMyLandingGear>> FarMotorToLandingGearMap;

        private Dictionary<IMyMotorStator, List<IMyCameraBlock>> MotorToCameraMap;
        private Dictionary<IMyMotorStator, IMyShipMergeBlock> MotorToMergeBlockMap;

        private Dictionary<IMyMotorStator, IEnumerator<bool>> MotorStatorToStowMovement1DEnumMap;
        private Dictionary<IMyMotorStator, IEnumerator<bool>> MotorStatorToUnStowMovement1DEnumMap;

        private Dictionary<IMyMotorStator, IEnumerator<bool>> MotorStatorToStowMovement2DEnumMap;
        private Dictionary<IMyMotorStator, IEnumerator<bool>> MotorStatorToUnStowMovement2DEnumMap;

        private IEnumerator<bool> CheckShipSystemsEnum;

        // Iterating lists: By "Iterating" we mean "Lists intended to be used in loops". Declaring these once and re-using them is performant!

        // TopGrid blocks - Blocks that are on the 'far' side of the hinge. The "top" grid, batman.
        private List<IMyLandingGear> NearMotorStatorLandingGearListIter;
        private List<IMyLandingGear> FarMotorStatorLandingGearListIter;
        private List<IMyCameraBlock> CameraListIter;

        // Indicates whether we are attempting to stow the guns or not. Toggled by PB argument/command.
        private bool StowingGuns = false;

        // Ugh, forgot. No Regex. Fuck.
        //Regex PicDiscriminatorRegex = new Regex("PIC(CTC|MOTOR|MERGE)?_?(\\d{0,3})?");
        //Match PicDiscriminatorMatchIter;

        public Program()
        {
            // The constructor, called only once every session and
            // always before any other method is called. Use it to
            // initialize your script. 
            //     
            // The constructor is optional and can be removed if not
            // needed.
            // 
            // It's recommended to set Runtime.UpdateFrequency 
            // here, which will allow your script to run itself without a 
            // timer block.
            Echo($"Picarl's CTC Setup Script Started INIT at {DateTime.Now.ToLongTimeString()}\n");
            // Init WCPBAPI
            WCAPI = new WcPbApi();

            try
            {
                while (!WCAPI.Activate(Me))
                {
                    try
                    {
                        WCAPI = new WcPbApi();
                        WCAPI.Activate(Me);
                    }
                    catch (Exception ex)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"Exception when initializing Weaponcore, Inner Loop!\n(This might be normal if the game just loaded.)\n{ex}\n", true, false);
                        }
                    }

                }
            } catch (Exception ex)
            {
                if (VerboseMode)
                {
                    ReportError($"Exception when initializing Weaponcore, Outer Loop!\n(This might be normal if the game just loaded.)\n{ex}\n",true,false);
                }
            }

            try
            {
                CTCSetupStage1();
            }
            catch (Exception ex)
            {
                Echo($"Exception thrown in SetupCTCs!\n{ex}");
            }

            Echo($"Picarl's CTC Setup Script Finished INIT at {DateTime.Now.ToLongTimeString()}");
        }

        public void Save()
        {
            // Called when the program needs to save its state. Use
            // this method to save your state to the Storage field
            // or some other means. 
            // 
            // This method is optional and can be removed if not
            // needed.
        }

        public void Main(string argument, UpdateType updateSource)
        {
            // The main entry point of the script, invoked every time
            // one of the programmable block's Run actions are invoked,
            // or the script updates itself. The updateSource argument
            // describes where the update came from. Be aware that the
            // updateSource is a  bitfield  and might contain more than 
            // one update type.
            // 
            // The method itself is required, but the arguments above
            // can be removed if not needed.

            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            // If we are being ran from anywhere non-terminal or non-trigger

            if (VerboseMode)
            {
                Echo($"StowingGuns: {StowingGuns}\n");
            }

            
            if (StowingGuns)
            {
                StowGuns();
            }
            else
            {
                UnStowGuns();
            }
            

            // If we are being ran from the terminal...
            if (updateSource == UpdateType.Terminal || updateSource == UpdateType.Trigger || updateSource == UpdateType.None)
            {
                if (argument.Equals("SETUP"))
                {
                    CTCSetupStage1();
                }

                if (argument.Equals("STOW"))
                {
                    this.StowingGuns = true;


                    if (StowingGuns)
                    {
                        if (VerboseMode)
                        {
                            Echo($"StowingGuns: {StowingGuns}\n");
                        }

                        StowGuns();
                    }
                }


                if (argument.Equals("UNSTOW"))
                {
                    this.StowingGuns = false;


                    if (StowingGuns)
                    {
                        if (VerboseMode)
                        {
                            Echo($"StowingGuns: {StowingGuns}\n");
                        }

                        UnStowGuns();
                    }

                }

                if (argument.Equals("RESET"))
                {
                    ResetCTCNames();

                    ResetMotorStatorNames();

                    ResetHingeTopGridCameraNames();
                }

                if (argument.Equals("RESETALL"))
                {
                    ResetCTCNames(true);

                    ResetMotorStatorNames(true);

                    ResetHingeTopGridCameraNames(true);
                }

                if (argument.Equals("AUTOASSIGN"))
                {
                    CTCSetupStage1(true);
                }
            }

        }

        public void StowGuns()
        {

            // StowGuns assumes that:
            // -> MotorToLandingGearMap is populated before you run it
            // -> 
            if (VerboseMode)
            {
                ReportError($"BEGIN: StowGuns\n", true, false);
                ReportError($"StowGuns Params:\n" +
                    $"CTCToNearMotorMap count {NearCTCToMotorMap.Count}\n" +
                    $"MotorToNearLandingGearMap count {NearMotorToLandingGearMap.Count}\n" +
                    $"MotorToFarLandingGearMap count {FarMotorToLandingGearMap.Count}\n" +
                    $"MotorStatorsNear count {MotorStatorsNear.Count}\n" +
                    $"MotorStatorsNearFarMap count {MotorStatorsNearFarMap.Count}\n", true, false);
            }

            try
            {
                try
                {
                    // Turn off the AI so we can control the MotorStators
                    foreach (IMyTurretControlBlock ctc in this.NearCTCToMotorMap.Keys)
                    {
                        ctc.Enabled = false;
                        ctc.AIEnabled = false;
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in StowGuns while attempting to iterate CTCToMotorMap!\n{ex}", true, false);
                }

                try
                {
                    // Iterate through MotorStators:LandingGear Map and check if any of the landingGears are locked. If not: Move the guns.
                    foreach (IMyMotorStator motorStator in MotorStatorsNear)
                    {
                        // Pre-Near Movement: If we have a 'far' stator associated - move that to zero first
                        /* Explanation of conditions:
                         * Format: (statement) --> (interpretation/explanation)
                         * 
                         * MotorStatorsNearFarMap.ContainsKey(motorStator) --> element presence check
                         * FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) --> element presence check
                        */

                        // debug output
                        if (VerboseMode)
                        {
                            ReportError($"StowGuns: Iterating NEAR MotorStator {motorStator.CustomName}\n" +
                                        $"Iterating motorStator ({motorStator.CustomName}) has landing gear associated with it:{NearMotorToLandingGearMap.ContainsKey(motorStator)}\n",
                                        true, false);
                        }

                        // Begin Near MotorStator search Movement
                        if ((NearMotorToLandingGearMap.ContainsKey(motorStator) &&
                            NearMotorToLandingGearMap[motorStator].Any(landingGear => landingGear.IsLocked == false) 
                            ||
                            (MotorStatorsNearFarMap.ContainsKey(motorStator) && 
                            FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) &&
                            FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Any(landingGear => landingGear.IsLocked == false))
                            ))
                        {
                            if (VerboseMode)
                            {
                                ReportError($"Moving MotorStator: {motorStator.CustomName} due to no locked landing gear detected!\n",true,false);
                            }

                            // Iterate through the landing gears and make sure autolock is on! (If a landing gear is present)
                            if (NearMotorToLandingGearMap.ContainsKey(motorStator))
                            {
                                foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                                {
                                    landingGear.AutoLock = true;
                                }
                            }

                            if (VerboseMode)
                            {
                                ReportError($"Autolock enabled for: {motorStator.CustomName}'s landing gears\n", true, false);
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:

                                        if (!MotorStatorToStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;
                                    case MotorStatorTypeEnum.Rotor:

                                        if (!MotorStatorToStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;
                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        if (!MotorStatorToStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;
                                    case MotorStatorTypeEnum.Mixed:

                                        if (!MotorStatorToStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"Error in StowGuns while attempting to MoveNext() MotorStatorToStowMovementEnumMap!\n{ex}\n", true, false);
                            }

                            // Lock the 'near' landing gears and rotors and begin checking / moving the 'far' landing gears and rotors
                            if (NearMotorToLandingGearMap.ContainsKey(motorStator))
                            {
                                foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                                {
                                    if (landingGear.IsLocked == true)
                                    {
                                        motorStator.RotorLock = true;
                                    }
                                    else
                                    {
                                        landingGear.AutoLock = true;
                                    }
                                }
                            }
                        }

                        // debug output
                        if (VerboseMode && MotorStatorsNearFarMap.ContainsKey(motorStator))
                        {
                            ReportError($"StowGuns: Iterating FAR Landing Gears and checking for locks!\n" +
                                $"Iterating motorStator ({MotorStatorsNearFarMap[motorStator]}) has landing gear associated with it:{FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator])}\n", true, false);
                        }
                        // Begin Far MotorStator search movement:
                        /* Explanation of conditions:
                         * Format: (statement) --> (interpretation/explanation)
                         * 
                         * RotateNearMotorStatorsOnlyDuringStow -> When stowing: Only move the 'near' MotorStator
                         * MotorStatorsNearFarMap.ContainsKey(motorStator) --> element presence check
                         * FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) --> element presence check
                         * FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Where(landingGear => landingGear.IsLocked == true).Count() == 0 --> Are there any 'far' landing gears lock? True if zero 'far' landing gears are locked.
                         * NearMotorToLandingGearMap[motorStator].Any(landingGear => landingGear.IsLocked == true) --> Is there at least one 'near' landing gear already locked?
                         * !NearMotorToLandingGearMap.ContainsKey(motorStator) --> There are no landing gears associated with the 'near' motorStator
                        */
                        if (MotorStatorsNearFarMap.ContainsKey(motorStator) &&
                                (
                                    (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) /*||
                                     (NearMotorToLandingGearMap.ContainsKey(motorStator) &&
                                      FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Where(landingGear => landingGear.IsLocked == true).Count() == 0
                                      )*/
                                    )
                                )
                            )
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Moving MotorStator: {MotorStatorsNearFarMap[motorStator].CustomName} due to no locked landing gear!\n", true, false);
                            }

                            // Iterate through the landing gears and make sure autolock is on!
                            if (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]))
                            {
                                foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                                {
                                    landingGear.AutoLock = true;
                                }
                            }

                            if (VerboseMode)
                            {
                                ReportError($"AutoLocked Enabled for {FarMotorToLandingGearMap.Count} MyLandingGear!\n", true, false);
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:

                                        if (RotateNearMotorStatorsOnlyDuringStow)
                                        {
                                            if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        else
                                        {
                                            if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        break;

                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        if (RotateNearMotorStatorsOnlyDuringStow)
                                        {
                                            if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        else
                                        {
                                            if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Rotor:

                                        if (RotateNearMotorStatorsOnlyDuringStow)
                                        {
                                            if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        else
                                        {
                                            if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Mixed:

                                        if (RotateNearMotorStatorsOnlyDuringStow)
                                        {
                                            if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        else
                                        {
                                            if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                            {
                                            }
                                        }
                                        break;
                                }

                                if (VerboseMode)
                                {
                                    ReportError($"StowGuns ran MoveNext()!\n", true, false);
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"Error in StowGuns while attempting to MoveNext() MotorStatorToUnStowMovementEnumMap!\n{ex}", true, false);
                            }

                            if (MotorStatorsNearFarMap.ContainsKey(motorStator) && FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]))
                            {
                                foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                                {
                                    if (landingGear.IsLocked == true)
                                    {
                                        MotorStatorsNearFarMap[motorStator].RotorLock = true;
                                    }
                                    else
                                    {
                                        landingGear.AutoLock = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            /* Explanation of conditions:
                             * MotorStatorsNearFarMap.ContainsKey(motorStator) --> element presence check
                             * FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) --> element presence check
                             * FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Where(landingGear => landingGear.IsLocked == true).Count() > 0) --> Are any of our 'far' landing gears already locked? At least one? True if > 0 'far' landing gears are locked.
                            */
                            if (MotorStatorsNearFarMap.ContainsKey(motorStator) &&
                                FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]))
                            {
                                // Iterate through the landing gears and make sure autolock is on!
                                foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                                {
                                    landingGear.AutoLock = true;
                                }

                                if (FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Where(landingGear => landingGear.IsLocked == true).Count() > 0)
                                {
                                    AssignMotorStatorOptions(MotorStatorsNearFarMap[motorStator], true);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in StowGuns while attempting to iterate MotorStatorsNear!\n{ex}\n", true, false);
                }

            }
            catch (Exception ex)
            {
                ReportError($"Error in StowGuns!\n{ex}\n", true, false);
            }

            if (VerboseMode)
            {
                ReportError($"END: StowGuns\n", true, false);
            }
        }

        public void UnStowGuns()
        {
            // StowGuns assumes that:
            // -> MotorToLandingGearMap is populated before you run it
            // -> 
            if (VerboseMode)
            {
                ReportError($"BEGIN: UnStowGuns\n", true, false);
            }

            try
            {
                try
                {
                    // Turn On the AI so the CTC can control the MotorStators
                    foreach (IMyTurretControlBlock ctc in this.NearCTCToMotorMap.Keys)
                    {
                        ctc.Enabled = true;
                        ctc.AIEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in UnStowGuns while attempting to iterate CTCToMotorMap!\n{ex}\n", true, false);
                }

                try
                {
                    // Iterate through MotorStators:LandingGear Map and check if any of the landingGears are locked. If not: Move the guns.
                    foreach (IMyMotorStator motorStator in MotorStatorsNear)
                    {
                        // verbose output
                        if (VerboseMode)
                        {
                            ReportError($"UnStow MotorStatorsNear Iteration: {motorStator.CustomName}\n", true, false);
                        }

                        AssignMotorStatorOptions(motorStator, false);

                        // verbose output for NEAR guard
                        if (VerboseMode)
                        {
                            ReportError($"UnStow NEAR Guard conditions met for {motorStator.CustomName}?\n" +
                                $"-----\n" +
                                $"(Math.Abs(RadiansToDegrees(motorStator.Angle)) ): {(Math.Abs(RadiansToDegrees(motorStator.Angle)))}\n" +
                                $"(Math.Abs(RadiansToDegrees(motorStator.Angle)) > StopAngleTolerance): {(Math.Abs(RadiansToDegrees(motorStator.Angle)) > StopAngleTolerance)}\n" +
                                $"-----\n\n"
                                , true, false);
                        }

                        // Iterate through the NEAR landing gears and make sure autolock is on!
                        if (NearMotorToLandingGearMap.ContainsKey(motorStator))
                        {
                            foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                            {
                                landingGear.AutoLock = false;
                                landingGear.Unlock();
                            }

                            if (VerboseMode)
                            {
                                ReportError($"AutoLock Disabled for {NearMotorToLandingGearMap.Count} NEAR IMyLandingGear! Gears unlocked!\n", true, false);
                            }
                        }

                        // Begin Near MotorStator movement:
                        if ((Math.Abs(RadiansToDegrees(motorStator.Angle)) > StopAngleTolerance))
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Unstowing MotorStator: {motorStator.CustomName}!\n", true, false);
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:

                                        motorStator.RotorLock = false;
                                        motorStator.Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;
                                    case MotorStatorTypeEnum.Rotor:

                                        motorStator.RotorLock = false;
                                        motorStator.Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;
                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        motorStator.RotorLock = false;
                                        motorStator.Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Mixed:

                                        motorStator.RotorLock = false;
                                        motorStator.Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"Error in UnStowGuns while attempting to MoveNext() MotorStatorToUnStowMovementEnumMap!\n{ex}\n", true, false);
                            }
                        }

                        // Switch from NEAR to FAR contexts!

                        // verbose output for FAR guard
                        if (VerboseMode)
                        {
                            ReportError($"UnStow FAR Guard conditions met for {motorStator.CustomName}?\n" +
                                $"-----\n" +
                                $"MotorStatorsNearFarMap.ContainsKey(motorStator): {MotorStatorsNearFarMap.ContainsKey(motorStator)}\n" +
                                $"(Math.Abs(MotorStatorsNearFarMap[motorStator].Angle) > StopAngleTolerance): {(MotorStatorsNearFarMap.ContainsKey(motorStator) ? Math.Abs(MotorStatorsNearFarMap[motorStator].Angle) : float.MinValue)}\n" +
                                $"(Math.Abs(MotorStatorsNearFarMap[motorStator].Angle) > StopAngleTolerance): {(MotorStatorsNearFarMap.ContainsKey(motorStator) ? (Math.Abs(MotorStatorsNearFarMap[motorStator].Angle) > StopAngleTolerance) : false)}\n" +
                                $"-----\n\n"
                                , true, false);
                        }

                        // Iterate through the FAR landing gears and make sure autolock is off!
                        if (MotorStatorsNearFarMap.ContainsKey(motorStator))
                        {

                            if (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]))
                            {
                                foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                                {
                                    landingGear.AutoLock = false;
                                    landingGear.Unlock();
                                }
                            }

                            if (VerboseMode)
                            {
                                ReportError($"AutoLock Disabled for {FarMotorToLandingGearMap.Count} FAR IMyLandingGear! Gears unlocked!\n", true, false);
                            }
                        }

                        // Begin Far MotorStator movement:
                        if (MotorStatorsNearFarMap.ContainsKey(motorStator) &&
                           (Math.Abs(MotorStatorsNearFarMap[motorStator].Angle) > StopAngleTolerance))
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Unstowing MotorStator: {MotorStatorsNearFarMap[motorStator].CustomName}!\n", true, false);
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:
                                        MotorStatorsNearFarMap[motorStator].RotorLock = false;
                                        MotorStatorsNearFarMap[motorStator].Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                        }
                                        break;

                                    case MotorStatorTypeEnum.AdvancedRotor:
                                        MotorStatorsNearFarMap[motorStator].RotorLock = false;
                                        MotorStatorsNearFarMap[motorStator].Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Rotor:
                                        MotorStatorsNearFarMap[motorStator].RotorLock = false;
                                        MotorStatorsNearFarMap[motorStator].Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Mixed:
                                        MotorStatorsNearFarMap[motorStator].RotorLock = false;
                                        MotorStatorsNearFarMap[motorStator].Displacement = UnStowDisplacement;

                                        if (!MotorStatorToUnStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                        }
                                        break;
                                }

                                if (VerboseMode)
                                {
                                    ReportError($"UnStowGuns ran MoveNext()!\n", true, false);
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"Error in StowGuns while attempting to MoveNext() MotorStatorToUnStowMovementEnumMap!\n{ex}", true, false);
                            }
                        }
                    }

                }
                catch (Exception ex)
                {
                    ReportError($"Error in UnStowGuns while attempting to iterate MotorStatorsNear!\n{ex}\n", true, false);
                }

            }
            catch (Exception ex)
            {
                ReportError($"Error in UnStowGuns!\n{ex}", true, false);
            }
        }

        public IEnumerator<bool> CheckShipSystems()
        {
            bool firstRun = true;

            while (true)
            {
                if (firstRun)
                {
                    CheckWeaponPower(true);
                    yield return true;
                    CheckCTCPower(true);
                    yield return true;
                    CheckHingePower(true);
                    yield return true;
                    firstRun = false;
                }
                CheckWeaponPower(true);
                yield return true;
                CheckCTCPower(!StowingGuns);
                yield return true;
                CheckHingePower(true);
                yield return true;
            }
        }

        public bool CheckCTCPower(bool state = true)
        {
            try
            {
                foreach (IMyTurretControlBlock ctc in this.CustomTurretControllers)
                {
                    ctc.Enabled = state;
                }
            }
            catch (Exception ex)
            {
                if (VerboseMode)
                {
                    ReportError($"Error in CheckCTCPower!\n{ex}\n", true, false);
                }
                return false;
            }
            return true;
        }

        public bool CheckHingePower(bool state = true)
        {
            try
            {
                foreach (IMyMotorStator motorStator in this.MotorStatorsNear)
                {
                    motorStator.Enabled = state;
                }

                foreach (IMyMotorStator motorStator in this.MotorStatorsNearFarMap.Values)
                {
                    motorStator.Enabled = state;
                }
            } catch (Exception ex)
            {
                if (VerboseMode)
                {
                    ReportError($"Error in CheckHingePower!\n{ex}\n", true, false);
                }
                return false;
            }
            return true;
        }

        public bool CheckWeaponPower(bool state = true)
        {
            foreach(IMyTerminalBlock weapon in WCStaticWeaponsTB.Union(WCTurretWeaponsTB))
            {
                try
                {
                    weapon.ApplyAction("OnOff_On");
                }
                catch (Exception ex)
                {
                    if (VerboseMode)
                    {
                        ReportError($"Error in CheckWeaponPower!\n{ex}\n",true,false);
                    }
                    return false;
                }
            }
            return true;
        }

        public bool CheckCTCPower()
        {
            foreach (IMyTurretControlBlock ctc in CustomTurretControllers)
            {
                try
                {
                    if (StowingGuns)
                    {
                        continue;
                    }
                    else
                    {
                        ctc.ApplyAction("OnOff_On");
                    }
                }
                catch (Exception ex)
                {
                    if (VerboseMode)
                    {
                        ReportError($"Error in CheckCTCPower!\n{ex}\n", true, false);
                    }
                    return false;
                }
            }
            return true;
        }

        public bool SetupWeaponcoreLists()
        {
            try
            {
                WCStaticWeapons = new List<MyDefinitionId>();
                WCTurretWeapons = new List<MyDefinitionId>();
                WCStaticWeaponsTB = new List<IMyTerminalBlock>();
                WCTurretWeaponsTB = new List<IMyTerminalBlock>();

                WCAPI.GetAllCoreStaticLaunchers(WCStaticWeapons);
                WCAPI.GetAllCoreTurrets(WCTurretWeapons);

                GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(WCStaticWeaponsTB, (myTerminalBlock) =>
                {
                    return WCStaticWeapons.Any(defId => defId.SubtypeName.Equals(myTerminalBlock.BlockDefinition.SubtypeName));
                });

                GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(WCTurretWeaponsTB, (myTerminalBlock) =>
                {
                    return WCTurretWeapons.Any(defId => defId.SubtypeName.Equals(myTerminalBlock.BlockDefinition.SubtypeName));
                });

                if (VerboseMode)
                {
                    ReportError($"SetupWeaponcoreLists:\n" +
                        $"Found {WCStaticWeapons.Count} static weapons.\n" +
                        $"Found {WCTurretWeapons.Count} turret weapons.\n", true, false);
                }

                return true;
            }
            catch (Exception ex)
            {
                ReportError($"Error in SetupWeaponcoreLists!\n{ex}\n",true,true);
                return false;
            }
        }
        // Hinge Stow movement will move the hinge to the desiredAngle, and if it stops or hits an obstruction - it will reverse.
        // Returns 'false' until it reaches desired angle.
        public IEnumerator<bool> StowMovementEnumerator(IMyMotorStator motorStator, float desiredAngle, float velocity, float impedenceDetectionThreshold, float stopAngleTolerance, bool reverseAfterStop = false)
        {
            Dictionary<IMyMotorStator, float> MotorStatorToLastAngleMap = new Dictionary<IMyMotorStator, float>();
            float lastCurrentAngleDiff = 0;

            while (true)
            {
                try
                {
                    // Calculate the lastCurrentAngleDiff, if we have a past angle.
                    lastCurrentAngleDiff = MotorStatorToLastAngleMap.ContainsKey(motorStator) ? (Math.Abs(MotorStatorToLastAngleMap[motorStator] - RadiansToDegrees(motorStator.Angle))) : 0;

                    // Check if the rotor might be stuck (compare against the previous angle)
                    if (MotorStatorToLastAngleMap.ContainsKey(motorStator))
                    {
                        if (reverseAfterStop)
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Hit STOW REVERSE condition at {DateTime.Now.ToLongTimeString()}\n" +
                                    $"AngleActual:{RadiansToDegrees(motorStator.Angle)}\n" +
                                    $"Last Angle:{MotorStatorToLastAngleMap[motorStator]}\n", false, false);
                            }

                            // This changes the desiredAngle to be the opposite of the current angle. AKA: "Reverse"

                            if ((lastCurrentAngleDiff < impedenceDetectionThreshold))
                            {
                                desiredAngle = -1 * desiredAngle;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in HingeStowMovementEnumerator -> Checking Reverse Condition!\n{ex}", true, false);
                }


                // Next: Check if that angle is the desired angle.
                if ((Math.Abs(RadiansToDegrees(motorStator.Angle) - desiredAngle)) > stopAngleTolerance)
                {
                    motorStator.RotorLock = false;

                    motorStator.RotateToAngle(MyRotationDirection.AUTO, desiredAngle, velocity);

                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        Echo("HingeStowMovementEnumerator returning true for:\n" +
                                "-----\n" +
                                $"{motorStator.CustomName}\n" +
                                $"angle: {RadiansToDegrees(motorStator.Angle)}\n" +
                                $"last angle: {MotorStatorToLastAngleMap[motorStator]}\n" +
                                $"desiredAngle: {desiredAngle}\n" +
                                $"velocity: {velocity}\n" +
                                $"reverseAfterStop: {reverseAfterStop}\n" +
                                $"ROTATE Command sent!\n" +
                                $"-----\n");
                    }
                }
                else
                {
                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        // Case where the current angle is the desiredAngle
                        Echo("HingeStowMovementEnumerator returning false for:\n" +
                            "-----\n" +
                            $"{motorStator.CustomName}\n" +
                            $"angle: {RadiansToDegrees(motorStator.Angle)}\n" +
                            $"last angle: {MotorStatorToLastAngleMap[motorStator]}\n" +
                            $"desiredAngle: {desiredAngle}\n" +
                            $"velocity: {velocity}\n" +
                            $"reverseAfterStop: {reverseAfterStop}\n" +
                            $"Rotation STOPPED!\n" +
                            $"-----\n");
                    }

                    motorStator.RotorLock = true;
                    yield return false;
                }

                try
                {
                    // Update the last motorStator angle with the current angle
                    if (!MotorStatorToLastAngleMap.ContainsKey(motorStator))
                    {
                        MotorStatorToLastAngleMap.Add(motorStator, RadiansToDegrees(motorStator.Angle));
                    }
                    else
                    {
                        // Otherwise: Assign the current angle to the "Last Angle Map"
                        MotorStatorToLastAngleMap[motorStator] = RadiansToDegrees(motorStator.Angle);
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in HingeStowMovementEnumerator -> Updating MotorStatorToLastAngleMap!\n{ex}", true, false);
                }

                yield return true;

            }
        }

        public IEnumerator<bool> UnStowMovementEnumerator(IMyMotorStator motorStator, float velocity, bool reverseAfterStop = false)
        {
            Dictionary<IMyMotorStator, float> MotorStatorToLastAngleMap = new Dictionary<IMyMotorStator, float>();

            while (true)
            {
                // Next: Check if that angle is the desired angle.
                if (RadiansToDegrees(motorStator.Angle) != 0 || Math.Abs(RadiansToDegrees(motorStator.Angle)) < StopAngleTolerance)
                {
                    motorStator.RotorLock = false;

                    motorStator.RotateToAngle(MyRotationDirection.AUTO, 0, velocity);

                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        if (VerboseMode)
                        {
                            ReportError("UnStowMovementEnumerator returning true for:\n" +
                                    "-----\n" +
                                    $"{motorStator.CustomName}\n" +
                                    $"angle: {RadiansToDegrees(motorStator.Angle)}\n" +
                                    $"last angle: {MotorStatorToLastAngleMap[motorStator]}\n" +
                                    $"desiredAngle: {0} (UNSTOW)\n" +
                                    $"velocity: {velocity}\n" +
                                    $"reverseAfterStop: {reverseAfterStop}\n" +
                                    $"ROTATE Command sent!\n" +
                                    $"-----\n", false, false);
                        }
                    }
                }
                else
                {
                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        // Case where the current angle is the desiredAngle
                        ReportError("UnStowMovementEnumerator returning false for:\n" +
                            "-----\n" +
                            $"{motorStator.CustomName}\n" +
                            $"angle: {RadiansToDegrees(motorStator.Angle)}\n" +
                            $"last angle: {MotorStatorToLastAngleMap[motorStator]}\n" +
                            $"desiredAngle: {0}\n" +
                            $"velocity: {velocity}\n" +
                            $"reverseAfterStop: {reverseAfterStop}\n" +
                            $"Rotation STOPPED!\n" +
                            $"-----\n", false, false);
                    }

                    AssignMotorStatorOptions(motorStator, false);

                    yield return false;
                }

                try
                {
                    // Update the last motorStator angle with the current angle
                    if (!MotorStatorToLastAngleMap.ContainsKey(motorStator))
                    {
                        MotorStatorToLastAngleMap.Add(motorStator, RadiansToDegrees(motorStator.Angle));
                    }
                    else
                    {
                        // Otherwise: Assign the current angle to the "Last Angle Map"
                        MotorStatorToLastAngleMap[motorStator] = RadiansToDegrees(motorStator.Angle);
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in HingeStowMovementEnumerator -> Updating MotorStatorToLastAngleMap!\n{ex}", true, false);
                }

                yield return true;

            }
        }

        public void ResetCTCNames(bool resetAll = false)
        {
            int counter = 0;

            try
            {
                if (resetAll)
                {
                    GridTerminalSystem.GetBlocksOfType<IMyTurretControlBlock>(this.CustomTurretControllers, (ctcBlock) => ctcBlock.IsSameConstructAs(Me));
                }

                foreach (IMyTurretControlBlock ctc in this.CustomTurretControllers)
                {
                    ctc.CustomName = ctc.DefinitionDisplayNameText + (counter == 0 ? "" : $" {counter}");
                    counter++;
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in ResetCTCNames!\n{ex}", true, true);
            }
        }

        public void ResetMotorStatorNames(bool resetAll = false)
        {
            int counter = 0;

            if (resetAll)
            {
                GridTerminalSystem.GetBlocksOfType<IMyMotorStator>(this.MotorStatorsNear, (motorStator) => motorStator.IsSameConstructAs(Me));
            }

            try
            {
                foreach (IMyMotorStator motorStator in this.MotorStatorsNear)
                {
                    motorStator.CustomName = motorStator.DefinitionDisplayNameText + (counter == 0 ? "" : $" {counter}");
                    counter++;
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in ResetMotorStatorNames!\n{ex}", true, true);
            }

        }

        public void ResetHingeTopGridCameraNames(bool resetAll = false)
        {
            int counter = 0;

            try
            {
                List<IMyCameraBlock> CameraList;

                foreach (IMyMotorStator motorStator in this.MotorStatorsNear)
                {
                    CameraList = new List<IMyCameraBlock>();
                    counter = 0;

                    if(motorStator.TopGrid != null)
                    {
                        GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraList, (cameraBlock) => cameraBlock.CubeGrid.IsSameConstructAs(motorStator.TopGrid));

                        if (resetAll)
                        {
                            GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraList, (cameraBlock) => cameraBlock.IsSameConstructAs(Me));
                        }

                        foreach (IMyCameraBlock cameraBlock in CameraList)
                        {
                            cameraBlock.CustomName = cameraBlock.DefinitionDisplayNameText + (counter == 0 ? "" : $" {counter}");
                            counter++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in ResetHingeTopGridCameraNames!\n{ex}", true, true);
            }
        }

        private void StandbyForWC()
        {
            // Init WCPBAPI
            WCAPI = new WcPbApi();

            try
            {
                while (!WCAPI.Activate(Me))
                {
                    try
                    {
                        WCAPI = new WcPbApi();
                        WCAPI.Activate(Me);
                    }
                    catch (Exception ex)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"Exception when initializing Weaponcore, Inner Loop!\n(This might be normal if the game just loaded.)\n{ex}\n", true, false);
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                if (VerboseMode)
                {
                    ReportError($"Exception when initializing Weaponcore, Outer Loop!\n(This might be normal if the game just loaded.)\n{ex}\n", true, false);
                }
            }
        }

        private void AssignMotorStatorOptions(IMyMotorStator motorStator, bool rotorLock = false)
        {
            // Assign Stator-Specific options here

            motorStator.Displacement = float.MaxValue;
            motorStator.Torque = MotorStatorTorque;
            motorStator.BrakingTorque = MotorStatorBrakingTorque;
            motorStator.LowerLimitDeg = float.MinValue;
            motorStator.UpperLimitDeg = float.MaxValue;
            motorStator.RotorLock = rotorLock;
            motorStator.TargetVelocityRPM = 0;
        }

        private void AssignCTCOptions(IMyTurretControlBlock ctc)
        {

            try
            {
                // Generic CTC Settings go here.
                WCAPI.SetBlockTrackingRange(ctc, TargetingRange);

                ctc.Range = TargetingRange;
                ctc.AIEnabled = true;
                ctc.IsSunTrackerEnabled = false;
                ctc.AngleDeviation = AngleDeviation;
                ctc.VelocityMultiplierAzimuthRpm = AzVelocityScalar;
                ctc.VelocityMultiplierElevationRpm = ElVelocityScalar;
                ctc.TargetCharacters = true;
                ctc.TargetLargeGrids = true;
                ctc.TargetSmallGrids = true;
                ctc.TargetStations = true;
                ctc.TargetNeutrals = false;
                ctc.Enabled = true;
                ctc.TargetMissiles = false;
                ctc.TargetMeteors = false;
                ctc.TargetFriends = false;

                //Echo($"Assigned settings to {ctc.CustomName}.\nTargeting Range:{TargetingRange}");
            }
            catch (Exception e)
            {
                ReportError($"Error while assigning CTC Options!\n{e}", true, true);

                //Echo($"Error while assigning CTC Options!\n{e}");
            }

        }

        private void ReportError(string dataToWrite, bool WriteToCustomData = false, bool clearCustomData = false)
        {
            if (WriteToCustomData)
            {
                if (clearCustomData)
                {
                    Me.CustomData = "";
                }

                Me.CustomData += dataToWrite;
            }
            else
            {
                Echo(dataToWrite);
            }
        }

        public void AssignCTCCamerasLoop(bool assignCameras, IMyTurretControlBlock ctc, IMyMotorStator motorStator)
        {
            try
            {
                // Find a camera on the subgrid associated with the IMotorStator, and assign it to the CTC (if working)
                //CameraListIter = new List<IMyCameraBlock>();

                // Populate the list of cameras on the subgrid
                //GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (camera) => (camera.CubeGrid.IsSameConstructAs(CTCToMotorMap.ElementAt(i).Value.TopGrid)));

                // Iterate through the camera blocks and append a tag to identify which IMyTurretControlBlock/IMyMotorStator combination they are associated with
                for (int j = 0; j < MotorToCameraMap[motorStator].Count; j++)
                {
                    // Echo($"Found Camera {CameraListIter.ElementAt(j).CustomName}, associating with {CTCToMotorDict.ElementAt(i).Key.CustomName}");
                    if (VerboseMode)
                    {
                        ReportError($"Entering CTCSetupStage2 -> Camera Naming and Assignment Loop Iter {j}!\n", true, false);
                    }

                    MotorToCameraMap[motorStator].ElementAt(j).CustomName = $"{CameraTag}-{j}";

                    // We'll just iterate-and-assign each Camera to the CTC as we find it. This will have the effect of ultimately assigning the 'last' camera to the CTC, but that shouldn't be an issue...

                    if (assignCameras)
                    {
                        ctc.Camera = CameraListIter.ElementAt(j);
                    }
                    else
                    {
                        ctc.Camera = null;
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in CTCSetupStage2 -> Camera Naming and Assignment!\n" +
                    $"MotorToCameraMap Count: {MotorToCameraMap.Count}\n" +
                    $"Current CTC:{ctc.CustomName}\n" +
                    $"CTCToMotorMap Count:{NearCTCToMotorMap.Count}\n" +
                    $"MotorStators Count: {MotorStatorsNear.Count}\n" +
                    $"Exception:\n{ex}\n"
                    , true, false);
            }
        }

        // AutoAssign: Automatically assign a name and increment, even without a [PICCTC] tag
        public void AssignCTCNames(bool autoAssign = false)
        {
            // Setup CustomName Naming
            try
            {
                for (int i = 0; i < CustomTurretControllers.Count; i++)
                {
                    //Echo($"Found Controller {i} with CustomName {TurretControllerList[i].CustomName}");

                    // We use '-' characters to designate a controller, and will use the same integer (ex: [PICCTC-1]) to associate pieces of equipment [PICMOTOR-1].
                    if (CustomTurretControllers[i].CustomName.Contains(CTCTag + "-"))
                    {
                        CustomTurretControllers[i].CustomName = "Custom Turret Controller " + CTCTag;
                    }

                    CustomTurretControllers[i].CustomName = autoAssign ? CustomTurretControllers[i].DefinitionDisplayNameText + $" {CTCTag}-{i}" : CustomTurretControllers[i].CustomName.Replace(CTCTag, $"{CTCTag}-{i}");
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error while assigning CTC Names!\n{ex}", true, true);
            }
        }

        public void AssignMotorStatorNames(bool autoAssign = false)
        {
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    if (VerboseMode)
                    {
                        ReportError($"Found MotorStatorsNear {i} with CustomName: {MotorStatorsNear[i].CustomName}\n", true, false);
                    }

                    if (MotorStatorsNear[i].CustomName.Contains(MotorStatorTag + "-"))
                    {
                        MotorStatorsNear[i].CustomName = MotorStatorDiscriminator + " " + MotorStatorTag;
                    }

                    // Assign the CustomName of the 'Near' MotorStator
                    MotorStatorsNear[i].CustomName = autoAssign ? MotorStatorsNear[i].DefinitionDisplayNameText + $" {MotorStatorTag}-{i}-NEAR" : (MotorStatorsNear[i].CustomName.Replace(MotorStatorTag, $"{MotorStatorTag}-{i}-NEAR"));

                }
                catch (Exception ex)
                {
                    ReportError($"Error when assigning Near MotorStator names!\n{ex}", true, false);
                }

                try
                {
                    // Next - If we have a 2-dimension rotor setup: Search for the 'far' MotorStators and name them.
                    // We use the MotorStatorsNearFarMap to check for the 'far' rotor and name it.
                    if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]))
                    {
                        if (MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName.Contains(MotorStatorTag + "-"))
                        {
                            MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName = MotorStatorDiscriminator + " " + MotorStatorTag;
                        }

                        // Assign the CustomName of the 'Near' MotorStator
                        MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName = autoAssign ? MotorStatorsNearFarMap[MotorStatorsNear[i]].DefinitionDisplayNameText + $" {MotorStatorTag}-{i}-FAR" : (MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName.Replace(MotorStatorTag, $"{MotorStatorTag}-{i}-FAR"));
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error when assigning Far MotorStator names!\n{ex}", true, false);
                }

            }
        }

        public void AssociateCTCsToMotorStators()
        {
            if (VerboseMode)
            {
                ReportError($"AssociateCTCsToMotorStators input counters, pre-completion:\n" +
                    $"TurretControllers:{CustomTurretControllers.Count}\n" +
                    $"MotorStatorsNear:{MotorStatorsNear.Count}\n"
                    , true, false);
            }

            // Iterate through the length of the MotorStatorList and each CTC to a MotorStator with a tag with a matching number
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    switch (MotorStatorDimensions)
                    {
                        case MotorStatorDimensionEnum.Azimuth:
                            NearCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNear[i]);

                            if (VerboseMode)
                            {
                                ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to MotorStator {MotorStatorsNear[i].CustomName}\n",true,false);
                            }

                            break;
                        case MotorStatorDimensionEnum.Elevation:
                            NearCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNear[i]);

                            if (VerboseMode)
                            {
                                ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to MotorStator {MotorStatorsNear[i].CustomName}\n", true, false);
                            }

                            break;

                        case MotorStatorDimensionEnum.ElevationAndAzimuth:

                            NearCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNear[i]);

                            if (VerboseMode)
                            {
                                ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to NEAR MotorStator {MotorStatorsNear[i].CustomName}\n", true, false);
                            }

                            try
                            {
                                if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]))
                                {
                                    FarCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNearFarMap[MotorStatorsNear[i]]);

                                    if (VerboseMode)
                                    {
                                        ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to FAR MotorStator {MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName}\n", true, false);
                                    }
                                }
                            } catch(Exception ex)
                            {
                                ReportError($"AssociateCTCsToMotorStators error: Unable to associate {CustomTurretControllers[i].CustomName} and {CustomTurretControllers[i].CustomName}/{(MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) ? MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName : "//NO FAR MOTORSTATOR DETECTED!//")}\n{ex}\n",true,false);
                            }
                            break;

                        case MotorStatorDimensionEnum.AzimuthAndElevation:


                            NearCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNear[i]);

                            if (VerboseMode)
                            {
                                ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to NEAR MotorStator {MotorStatorsNear[i].CustomName}\n", true, false);
                            }

                            try
                            {
                                if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]))
                                {
                                    FarCTCToMotorMap.Add(CustomTurretControllers[i], MotorStatorsNearFarMap[MotorStatorsNear[i]]);

                                    if (VerboseMode)
                                    {
                                        ReportError($"AssociateCTCsToMotorStators: Associated CTC {CustomTurretControllers[i].CustomName} to FAR MotorStator {MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName}\n", true, false);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"AssociateCTCsToMotorStators error: Unable to associate {CustomTurretControllers[i].CustomName} and {CustomTurretControllers[i].CustomName}/{(MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) ? MotorStatorsNearFarMap[MotorStatorsNear[i]].CustomName : "//NO FAR MOTORSTATOR DETECTED!//")}\n{ex}\n", true, false);
                            }
                            break;
                    }
                }
                catch (Exception e)
                {
                    ReportError($"Error pairing CTCs to MotorStators on iteration {i}!\n{e}", true, false);
                    //Echo($"Error pairing CTCs to MotorStators!\n{e}");
                }
            }

            if (VerboseMode)
            {
                ReportError($"AssociateCTCsToMotorStators list counters, post-completion:\n" +
                    $"NearCTCToMotorMap:{NearCTCToMotorMap.Count}\n" +
                    $"FarCTCToMotorMap:{FarCTCToMotorMap.Count}\n"
                    , true, false);
            }
        }

        public void AssociateNearFarMotorStators()
        {
            try
            {
                if (VerboseMode)
                {
                    ReportError($"Attempting to search MotorStatorsNear (Count:{MotorStatorsNear.Count}) list for 'Far' MotorStators.\n", true, false);
                }

                List<IMyMotorStator> farStatorListIter;

                foreach (IMyMotorStator nearStator in this.MotorStatorsNear)
                {
                    farStatorListIter = new List<IMyMotorStator>();

                    // Add filters to farStators here
                    GridTerminalSystem.GetBlocksOfType<IMyMotorStator>(farStatorListIter, (farStatorCandidate) =>
                    nearStator.TopGrid != null &&
                    nearStator.TopGrid.EntityId == farStatorCandidate.CubeGrid.EntityId &&
                    nearStator.IsWorking &&
                    farStatorCandidate.IsWorking
                    );

                    if (farStatorListIter.Count > 0)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"Associating {farStatorListIter.First().CustomName} far stator with near stator {nearStator.CustomName}\n", true, false);
                        }

                        this.MotorStatorsNearFarMap.Add(nearStator, farStatorListIter.First());
                    }
                    else
                    {
                        if (VerboseMode)
                        {
                            ReportError($"Found zero far stators associated with near stator {nearStator.CustomName}\n", true, false);
                        }
                    }
                }

                if (VerboseMode)
                {
                    ReportError($"AssociateNearFarMotorStators populated MotorStatorsNearFarMap with {MotorStatorsNearFarMap.Count} entries!\n", true, false);
                }

            }
            catch (Exception ex)
            {
                ReportError($"Error in AssociateNearFarMotorStators!\n{ex}\n", true, false);
            }

        }

        public void AssociateMotorStatorsAndCameras()
        {
            // Iterate through the length of the MotorStatorList and each Camera to a MotorStator with a tag with a matching number
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    // First: We attempt to get the cameras that exist on the near subgrid
                    CameraListIter = new List<IMyCameraBlock>();

                    GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (cameraBlock) => MotorStatorsNear[i].TopGrid != null && cameraBlock.CubeGrid.EntityId == (MotorStatorsNear[i].TopGrid.EntityId));

                    if (VerboseMode)
                    {
                        ReportError($"AssociateMotorStatorsAndCameras: Associating list of {CameraListIter.Count} NEAR entries with {MotorStatorsNear[i].CustomName}\n", true, false);
                    }

                    if (CameraListIter.Count == 0)
                    {
                        MotorToCameraMap.Add(MotorStatorsNear[i], new List<IMyCameraBlock>());
                    }
                    else
                    {
                        foreach (IMyCameraBlock cameraBlock in CameraListIter)
                        {
                            if (MotorToCameraMap.ContainsKey(MotorStatorsNear[i]))
                            {
                                MotorToCameraMap[MotorStatorsNear[i]].Add(cameraBlock);
                            }
                            else
                            {
                                MotorToCameraMap.Add(MotorStatorsNear[i], new List<IMyCameraBlock> { cameraBlock });
                            }
                        }
                    }

                    // Next: We attempt to get the cameras that exist on the far subgrid.
                    // Begin by checking if there is a 'far' grid associated with the current 'near' rotor.
                    if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]))
                    {
                        GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (cameraBlock) => MotorStatorsNear[i].TopGrid != null && cameraBlock.CubeGrid.EntityId == (MotorStatorsNearFarMap[MotorStatorsNear[i]].TopGrid.EntityId));

                        if (VerboseMode)
                        {
                            ReportError($"AssociateMotorStatorsAndCameras: Associating list of {CameraListIter.Count} FAR entries with {MotorStatorsNear[i].CustomName}\n", true, false);
                        }

                        if (CameraListIter.Count == 0)
                        {
                            if (MotorToCameraMap.ContainsKey(MotorStatorsNear[i]))
                            {
                                MotorToCameraMap[MotorStatorsNear[i]] = new List<IMyCameraBlock>();
                            }
                            else
                            {
                                MotorToCameraMap.Add(MotorStatorsNear[i], new List<IMyCameraBlock>());
                            }
                        }
                        else
                        {
                            foreach (IMyCameraBlock cameraBlock in CameraListIter)
                            {
                                if (MotorToCameraMap.ContainsKey(MotorStatorsNear[i]))
                                {
                                    MotorToCameraMap[MotorStatorsNear[i]].Add(cameraBlock);
                                }
                                else
                                {
                                    MotorToCameraMap.Add(MotorStatorsNear[i], new List<IMyCameraBlock> { cameraBlock });
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    ReportError($"Error in AssociateMotorStatorsAndCameras!\n{e}", true, false);
                }
            }
        }

        public void AssociateMotorStatorsStwMvmntEnumerators()
        {
            foreach (IMyMotorStator motorStator in MotorStatorsNear)
            {
                switch (this.MotorStatorDimensions)
                {
                    case MotorStatorDimensionEnum.Azimuth:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));
                        break;

                    case MotorStatorDimensionEnum.Elevation:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));
                        break;

                    case MotorStatorDimensionEnum.AzimuthAndElevation:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));

                        if (MotorStatorsNearFarMap.ContainsKey(motorStator))
                        {
                            MotorStatorToStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], StowMovementEnumerator(MotorStatorsNearFarMap[motorStator], StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                            MotorStatorToUnStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], UnStowMovementEnumerator(MotorStatorsNearFarMap[motorStator], 5, true));
                        }
                        break;

                    case MotorStatorDimensionEnum.ElevationAndAzimuth:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));

                        if (MotorStatorsNearFarMap.ContainsKey(motorStator))
                        {
                            MotorStatorToStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], StowMovementEnumerator(MotorStatorsNearFarMap[motorStator], StowArc, 5, ImpedenceDetectionThreshold, StopAngleTolerance, true));
                            MotorStatorToUnStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], UnStowMovementEnumerator(MotorStatorsNearFarMap[motorStator], 5, true));
                        }
                        break;
                }
            }

            if (VerboseMode)
            {
                ReportError("AssociateMotorStatorsStwMvmntEnumerators list status:\n", true, false);

                ReportError($"MotorStatorToStowMovement1DEnumMap count: {MotorStatorToStowMovement1DEnumMap.Count}:\n", true, false);
                ReportError($"MotorStatorToUnStowMovement1DEnumMap count: {MotorStatorToUnStowMovement1DEnumMap.Count}:\n", true, false);

                ReportError($"MotorStatorToStowMovement2DEnumMap count: {MotorStatorToStowMovement2DEnumMap.Count}:\n", true, false);
                ReportError($"MotorStatorToUnStowMovement2DEnumMap count: {MotorStatorToUnStowMovement2DEnumMap.Count}:\n", true, false);
            }
        }

        public void AssociateMotorStatorsAndLandingGears()
        {

            if (VerboseMode)
            {
                ReportError($"BEGIN: AssociateMotorStatorsAndLandingGears\n", true, false);

                ReportError($"MotorStatorsNearFarMap has {MotorStatorsNearFarMap.Count} entries.\n" +
                            $"MotorStatorsNear has {MotorStatorsNear.Count} entries.\n", true, false);
            }

            // Iterate through the length of the MotorStatorList and each CTC to a MotorStator with a tag with a matching number
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    if (VerboseMode)
                    {
                        ReportError($"AssociateMotorStatorsAndLandingGears Iteration: {MotorStatorsNear[i].CustomName}\n", true, false);
                    }

                    NearMotorStatorLandingGearListIter = new List<IMyLandingGear>();
                    FarMotorStatorLandingGearListIter = new List<IMyLandingGear>();

                    GridTerminalSystem.GetBlocksOfType<IMyLandingGear>(NearMotorStatorLandingGearListIter, (landingGear) => MotorStatorsNear[i].TopGrid != null && landingGear.CubeGrid.EntityId == (MotorStatorsNear[i].TopGrid.EntityId));

                    if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]))
                    {
                        GridTerminalSystem.GetBlocksOfType<IMyLandingGear>(FarMotorStatorLandingGearListIter, (landingGear) => MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) && MotorStatorsNearFarMap[MotorStatorsNear[i]].TopGrid != null && landingGear.CubeGrid.EntityId == (MotorStatorsNearFarMap[MotorStatorsNear[i]].TopGrid.EntityId));
                    }

                    if (VerboseMode)
                    {
                        ReportError($"AssociateMotorStatorsAndLandingGears: Found {NearMotorStatorLandingGearListIter.Count} NEAR Landing Gears!\n" +
                                $"AssociateMotorStatorsAndLandingGears: Found {FarMotorStatorLandingGearListIter.Count} FAR Landing Gears!\n"
                                , true, false);
                    }

                    foreach (IMyLandingGear landingGear in NearMotorStatorLandingGearListIter)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"AssociateMotorStatorsAndLandingGears Iterating NEAR NearMotorStatorLandingGearListIter: {landingGear.CustomName}\n", true, false);
                        }

                        if (NearMotorToLandingGearMap.ContainsKey(MotorStatorsNear[i]))
                        {
                            NearMotorToLandingGearMap[MotorStatorsNear[i]].Add(landingGear);
                        }
                        else
                        {
                            NearMotorToLandingGearMap.Add(MotorStatorsNear[i], new List<IMyLandingGear> { landingGear });
                        }
                    }


                    foreach (IMyLandingGear landingGear in FarMotorStatorLandingGearListIter)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"AssociateMotorStatorsAndLandingGears Iterating FAR landingGear named: {landingGear.CustomName}\n", true, false);
                        }

                        if (VerboseMode)
                        {
                            ReportError($"AssociateMotorStatorsAndLandingGears met condition MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]):{MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i])}\n", true, false);
                            ReportError($"AssociateMotorStatorsAndLandingGears met condition FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[MotorStatorsNear[i]]):{(MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) ? FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[MotorStatorsNear[i]]) : false)}\n", true, false);
                        }

                        if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) &&
                            FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[MotorStatorsNear[i]]))
                        {
                            if (VerboseMode)
                            {
                                ReportError($"AssociateMotorStatorsAndLandingGears Entered Far 'True' condition\n", true, false);
                            }

                            FarMotorToLandingGearMap[MotorStatorsNearFarMap[MotorStatorsNear[i]]].Add(landingGear);

                            if (VerboseMode)
                            {
                                ReportError($"AssociateMotorStatorsAndLandingGears Exited Far 'True' condition\n", true, false);
                            }
                        }
                        else
                        {
                            if (MotorStatorsNearFarMap.ContainsKey(MotorStatorsNear[i]) &&
                                !FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[MotorStatorsNear[i]]))
                            {
                                if (VerboseMode)
                                {
                                    ReportError($"AssociateMotorStatorsAndLandingGears Entered Far 'False' condition\n", true, false);
                                }

                                FarMotorToLandingGearMap.Add(MotorStatorsNearFarMap[MotorStatorsNear[i]], new List<IMyLandingGear> { landingGear });

                                if (VerboseMode)
                                {
                                    ReportError($"AssociateMotorStatorsAndLandingGears Exited Far 'False' condition\n", true, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    ReportError($"Error in AssociateMotorStatorsAndLandingGears!\n{e}", true, false);
                    //Echo($"Error pairing CTCs to MotorStators!\n{e}");
                }
            }

            if (VerboseMode)
            {
                ReportError($"AssociateMotorStatorsAndLandingGears: Populated {NearMotorToLandingGearMap.Count} NEAR Landing Gears!\n", true, false);
                ReportError($"AssociateMotorStatorsAndLandingGears: Populated {FarMotorToLandingGearMap.Count} FAR Landing Gears!\n", true, false);
                ReportError($"END: AssociateMotorStatorsAndLandingGears\n", true, false);
            }
        }

        public void CTCSetupStage2(bool assignCameras = true)
        {
            try
            {
                if (VerboseMode)
                {
                    ReportError($"BEGIN CTCSetupStage2\n" +
                                $"Near MotorStator Map Count:{NearCTCToMotorMap.Count}\n" +
                                $"Far MotorStator Map Count:{FarCTCToMotorMap.Count}\n", true, false);
                }

                // Iterate through each IMyTurretControlBlock:IMyMotorStator pair and assign Rotors and Cameras as appropriate
                foreach (KeyValuePair<IMyTurretControlBlock,IMyMotorStator> nearCTCKVP in NearCTCToMotorMap)
                {
                    try
                    {
                        // Echo($"Attempting to assign MotorStators to CTC El/Az Rotors");
                        // Determine which Rotors (IMyMotorStators) to assign

                        // Empty the field, first
                        nearCTCKVP.Key.ElevationRotor = null;
                        nearCTCKVP.Key.AzimuthRotor = null;

                        if (VerboseMode)
                        {
                            ReportError($"Attempting to associate CTC {nearCTCKVP.Key.CustomName} with...\n" +
                                $"Near MotorStator:{nearCTCKVP.Value.CustomName}\n" +
                                $"Far MotorStator {(FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) ? FarCTCToMotorMap[nearCTCKVP.Key].CustomName : "KEY NOT FOUND")}\n",true,false);
                        }
                        switch (this.MotorStatorDimensions)
                        {
                            // Note: Oddly enough - When setting up a CTC turret that uses only a single dimension (Az or El): We are required to still set the CTC to use that single MotorStator as the Elevation AND Azimuth rotor.
                            case MotorStatorDimensionEnum.Azimuth:

                                if (VerboseMode)
                                {
                                    ReportError($"Attempting to assign CTC {nearCTCKVP.Key.CustomName}\n the following MotorStators:" +
                                        $"ElevationRotor MotorStator:{nearCTCKVP.Value.CustomName}\n" +
                                        $"AzimuthRotor MotorStator {nearCTCKVP.Value.CustomName}\n", true, false);
                                }

                                nearCTCKVP.Key.ElevationRotor = nearCTCKVP.Value;
                                nearCTCKVP.Key.AzimuthRotor = nearCTCKVP.Value;

                                break;

                            case MotorStatorDimensionEnum.Elevation:

                                if (VerboseMode)
                                {
                                    ReportError($"Attempting to assign CTC {nearCTCKVP.Key.CustomName}\n the following MotorStators:" +
                                        $"ElevationRotor MotorStator:{nearCTCKVP.Value.CustomName}\n" +
                                        $"AzimuthRotor MotorStator {nearCTCKVP.Value.CustomName}\n", true, false);
                                }

                                nearCTCKVP.Key.ElevationRotor = nearCTCKVP.Value;
                                nearCTCKVP.Key.AzimuthRotor = nearCTCKVP.Value;

                                break;

                            case MotorStatorDimensionEnum.AzimuthAndElevation:

                                
                                try
                                {

                                    if (VerboseMode)
                                    {
                                        ReportError($"Attempting to assign CTC {nearCTCKVP.Key.CustomName} the following MotorStators:\n" +
                                            $"ElevationRotor MotorStator:{nearCTCKVP.Value.CustomName}\n" +
                                            $"AzimuthRotor MotorStator {(FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) ? FarCTCToMotorMap[nearCTCKVP.Key].CustomName : "KEY NOT FOUND")}\n", true, false);
                                    }

                                    nearCTCKVP.Key.AzimuthRotor = nearCTCKVP.Value;
                                    nearCTCKVP.Key.ElevationRotor = FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) ? FarCTCToMotorMap[nearCTCKVP.Key] : nearCTCKVP.Value; ;
                                }
                                catch (Exception ex)
                                {
                                    nearCTCKVP.Key.ElevationRotor = nearCTCKVP.Value;
                                    ReportError($"Failed to pair Az/El (Second) dimension MotorStator for {nearCTCKVP.Value.CustomName}!\n" +
                                        $"Assigning El {nearCTCKVP.Key.CustomName} to {nearCTCKVP.Value.CustomName} " +
                                        $"{ex}\n", true, false);
                                }

                                break;
                            case MotorStatorDimensionEnum.ElevationAndAzimuth:


                                try
                                {

                                    if (VerboseMode)
                                    {
                                        ReportError($"Attempting to assign CTC {nearCTCKVP.Key.CustomName} the following MotorStators:\n" +
                                            $"ElevationRotor MotorStator:{nearCTCKVP.Value.CustomName}\n" +
                                            $"AzimuthRotor MotorStator {(FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) ? FarCTCToMotorMap[nearCTCKVP.Key].CustomName : "KEY NOT FOUND")}\n", true, false);
                                    }

                                    nearCTCKVP.Key.ElevationRotor = nearCTCKVP.Value;
                                    nearCTCKVP.Key.AzimuthRotor = FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) ? FarCTCToMotorMap[nearCTCKVP.Key] : nearCTCKVP.Value;
                                }
                                catch (Exception ex)
                                {
                                    nearCTCKVP.Key.AzimuthRotor = nearCTCKVP.Value;
                                    ReportError($"Failed to pair Az/El (Second) dimension MotorStator for {nearCTCKVP.Value.CustomName}!\n" +
                                        $"Assigning Az {nearCTCKVP.Key.CustomName} to {nearCTCKVP.Value.CustomName} " +
                                        $"{ex}\n", true, false);
                                }

                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> Rotor Assignment!\n{ex}", true, false);
                    }


                    try
                    {
                        AssignCTCOptions(nearCTCKVP.Key);

                    }
                    catch (Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> AssignCTCOptions!\n{ex}", true, false);
                    }

                    try
                    {
                        AssignMotorStatorOptions(nearCTCKVP.Value);

                        if (MotorStatorsNearFarMap.ContainsKey(nearCTCKVP.Value))
                        {
                            AssignMotorStatorOptions(MotorStatorsNearFarMap[nearCTCKVP.Value]);
                        }

                    }
                    catch (Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> AssignMotorStatorOptions!\n{ex}", true, false);
                    }

                    // Assign near cameras if the 'near' motor associated with the CTC has any...
                    AssignCTCCamerasLoop(assignCameras, nearCTCKVP.Key, nearCTCKVP.Value);

                    // Assign 'far' cameras if the 'near' motor associated with the CTC has a 'far' MotorStator associated with it...
                    if(MotorStatorsNearFarMap.ContainsKey(nearCTCKVP.Value) && 
                        FarCTCToMotorMap.ContainsKey(nearCTCKVP.Key) && 
                        MotorToCameraMap.ContainsKey(MotorStatorsNearFarMap[nearCTCKVP.Value]))
                    {
                        AssignCTCCamerasLoop(assignCameras, nearCTCKVP.Key, nearCTCKVP.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in CTCSetupStage2!\n{ex}", true, false);
            }

        }

        public void CTCSetupStage1(bool autoAssign = false)
        {
            StandbyForWC();

            // Instantiate Variables
            CustomTurretControllers = new List<IMyTurretControlBlock>();
            MotorStatorsNear = new List<IMyMotorStator>();
            MergeBlocks = new List<IMyShipMergeBlock>();
            NearMotorStatorLandingGearListIter = new List<IMyLandingGear>();

            NearCTCToMotorMap = new Dictionary<IMyTurretControlBlock, IMyMotorStator>();
            FarCTCToMotorMap = new Dictionary<IMyTurretControlBlock, IMyMotorStator>();

            MotorStatorsNearFarMap = new Dictionary<IMyMotorStator, IMyMotorStator>();

            MotorToMergeBlockMap = new Dictionary<IMyMotorStator, IMyShipMergeBlock>();
            MotorToCameraMap = new Dictionary<IMyMotorStator, List<IMyCameraBlock>>();

            NearMotorToLandingGearMap = new Dictionary<IMyMotorStator, List<IMyLandingGear>>();
            FarMotorToLandingGearMap = new Dictionary<IMyMotorStator, List<IMyLandingGear>>();

            MotorStatorToStowMovement1DEnumMap = new Dictionary<IMyMotorStator, IEnumerator<bool>>();
            MotorStatorToUnStowMovement1DEnumMap = new Dictionary<IMyMotorStator, IEnumerator<bool>>();

            MotorStatorToStowMovement2DEnumMap = new Dictionary<IMyMotorStator, IEnumerator<bool>>();
            MotorStatorToUnStowMovement2DEnumMap = new Dictionary<IMyMotorStator, IEnumerator<bool>>();

            CheckShipSystemsEnum = CheckShipSystems();

            // Populate Lists of trivially-obtainable blocks
            GridTerminalSystem.GetBlocksOfType<IMyTurretControlBlock>(CustomTurretControllers, (ctcBlock) => autoAssign ? ctcBlock.IsSameConstructAs(Me) : ctcBlock.CustomName.Contains(CTCTag) && ctcBlock.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType<IMyMotorStator>(MotorStatorsNear, (motorBlock) => autoAssign ? motorBlock.CubeGrid.EntityId == Me.CubeGrid.EntityId : motorBlock.CustomName.Contains(MotorStatorTag) && motorBlock.CubeGrid.EntityId == Me.CubeGrid.EntityId);
            GridTerminalSystem.GetBlocksOfType<IMyShipMergeBlock>(MergeBlocks, (mergeBlock) => autoAssign ? mergeBlock.IsSameConstructAs(Me) : mergeBlock.CustomName.Contains(MotorStatorTag) && mergeBlock.CustomName.Contains(MergeBlockTag) && mergeBlock.IsSameConstructAs(Me));

            if (VerboseMode)
            {
                ReportError($"List Counts:\n" +
                    $"TurretControllers: {CustomTurretControllers.Count}\n" +
                    $"MotorStators: {MotorStatorsNear.Count}\n" +
                    $"MergeBlocks: {MergeBlocks.Count}\n",
                    true, false);
            }

            // This guard is important!
            // This script relies on many assumptions.
            // An assumption that you have at one TurretController for each MotorStator.
            // You can have more TurretControllers, but we are assuming that setup will be ran on a meme-CTC grid.
            // TurretControllers >= MotorStators (Rotors,Adv. Rotors, Hinges)
            if (CustomTurretControllers.Count < MotorStatorsNear.Count)
            {
                ReportError($"Error in CTCSetupStage1:\nInvalid ratio of CTC's ({CustomTurretControllers.Count}) to MotorStators ({MotorStatorsNear.Count})!\nNeed to have at least one CTC per MotorStator (Hinge, Rotor, etc)!", true, false);
                // Echo($"Error in CTCSetupStage1: Invalid ratio of CTC's ({TurretControllers.Count}) to MotorStators ({MotorStators.Count})!\nNeed to have at least one CTC per MotorStator (Hinge, Rotor, etc)!");
                return;
            }

            try
            {
                AssignCTCNames(true);

            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssignCTCNames Throw!\n{ex}\n", true, true);
            }

            try
            {
                AssociateNearFarMotorStators();
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssociateNearFarMotorStators Throw!\n{ex}\n", true, true);
            }

            try
            {
                AssignMotorStatorNames(true);
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssignMotorStatorNames Throw!\n{ex}\n", true, true);
            }

            try
            {
                AssociateCTCsToMotorStators();
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssociateCTCsToMotorStators Throw!\n{ex}\n", true, true);
            }

            try
            {

                AssociateMotorStatorsAndLandingGears();

            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssociateMotorStatorsAndLandingGears Throw!\n{ex}\n", true, true);
            }

            try
            {
                AssociateMotorStatorsAndCameras();
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssociateMotorStatorsAndCameras Throw!\n{ex}\n", true, true);
            }

            try
            {
                AssociateMotorStatorsStwMvmntEnumerators();
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: AssociateMotorStatorsStwMvmntEnumerators Throw!\n{ex}\n", true, true);
            }

            try
            {
                CTCSetupStage2(false);
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: CTCSetupStage2 Throw!\n{ex}\n", true, true);
            }
        }

        public float RadiansToDegrees(float radians)
        {
            return (float)(radians * (180 / Math.PI));
        }
    }
}
