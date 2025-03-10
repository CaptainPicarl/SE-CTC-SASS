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

        // Picarl's CTC Setup-And-Stow Script
        public enum MotorStatorTypeEnum
        {
            Hinge,
            Rotor,
            AdvancedRotor
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

        public MotorStatorTypeEnum MotorStatorDiscriminator = MotorStatorTypeEnum.Hinge;
        public MotorStatorDimensionEnum MotorStatorDimensions = MotorStatorDimensionEnum.ElevationAndAzimuth;

        public float TargetingRange = float.MaxValue;

        public float StowArc = 180f;

        private WcPbApi WCAPI;

        private string CTCTag = "[PICCTC]";
        private string MotorStatorTag = "[PICMOTOR]";
        private string MergeBlockTag = "[PICMERGE]";
        private string CameraTag = "[PICCAM]";

        private List<IMyTurretControlBlock> TurretControllers;

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

        // Iterating lists: By "Iterating" we mean "Lists intended to be used in loops". Declaring these once and re-using them is performant!

        // TopGrid blocks - Blocks that are on the 'far' side of the hinge. The "top" grid, batman.
        private List<IMyLandingGear> NearMotorStatorLandingGearListIter;
        private List<IMyLandingGear> FarMotorStatorLandingGearListIter;
        private List<IMyCameraBlock> CameraListIter;

        // Indicates whether we are attempting to stow the guns or not. Toggled by PB argument/command.
        private bool StowingGuns = false;

        //General Verbose Mode (Writes to CustomData)
        private bool VerboseMode = true;

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
            WCAPI.Activate(Me);

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
                    if (VerboseMode)
                    {
                        ReportError($"StowGuns: Iterating Landing Gears and checking for locks!\n", true, false);
                    }

                    // Iterate through MotorStators:LandingGear Map and check if any of the landingGears are locked. If not: Move the guns.
                    foreach(IMyMotorStator motorStator in MotorStatorsNear)
                    {

                        if (NearMotorToLandingGearMap.ContainsKey(motorStator) &&
                            NearMotorToLandingGearMap[motorStator].Any(landingGear => landingGear.IsLocked == false))
                        {
                            if (VerboseMode)
                            {
                                Echo($"Moving MotorStator: {motorStator.CustomName} due to no locked landing gear!\n");
                            }

                            // Iterate through the landing gears and make sure autolock is on!
                            foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                            {
                                landingGear.AutoLock = true;
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
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportError($"Error in StowGuns while attempting to MoveNext() MotorStatorToStowMovementEnumMap!\n{ex}\n", true, false);
                            }

                            // Lock the 'near' landing gears and rotors and begin checking / moving the 'far' landing gears and rotors
                            foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                            {
                                if (landingGear.IsLocked == true)
                                {
                                    motorStator.RotorLock = true;
                                }
                            }
                        }

                        if (VerboseMode)
                        {
                            ReportError($"StowGuns: Iterating FAR Landing Gears and checking for locks!\n" +
                                $"Iterating motorStator ({MotorStatorsNearFarMap[motorStator]}) has landing gear associated with it:{FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator])}\n", true, false);
                        }

                        // Begin Far MotorStator movement:
                        if (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) && 
                            FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Any(landingGear => landingGear.IsLocked == false))
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Moving MotorStator: {MotorStatorsNearFarMap[motorStator].CustomName} due to no locked landing gear!\n", true, false);
                            }

                            // Iterate through the landing gears and make sure autolock is on!
                            foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                            {
                                landingGear.AutoLock = true;
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

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;

                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Rotor:

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            motorStator.RotorLock = true;
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
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in StowGuns while attempting to iterate MotorToLandingGearMap!\n{ex}\n", true, false);
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
                        motorStator.RotorLock = false;

                        if (NearMotorToLandingGearMap.ContainsKey(motorStator) && 
                            NearMotorToLandingGearMap[motorStator].Any(landingGear => landingGear.IsLocked == true))
                        {
                            if (VerboseMode)
                            {
                                Echo($"Moving MotorStator: {motorStator.CustomName} due to no locked landing gear!\n");
                            }

                            // Iterate through the landing gears and make sure autolock is on!
                            foreach (IMyLandingGear landingGear in NearMotorToLandingGearMap[motorStator])
                            {
                                landingGear.AutoLock = false;
                                landingGear.Unlock();
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:

                                        motorStator.RotorLock = false;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;
                                    case MotorStatorTypeEnum.Rotor:

                                        motorStator.RotorLock = false;

                                        if (!MotorStatorToUnStowMovement1DEnumMap[motorStator].MoveNext())
                                        {
                                        }
                                        break;
                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        motorStator.RotorLock = false;

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

                        // Begin Far MotorStator movement:
                        if (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[motorStator]) &&
                            FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]].Any(landingGear => landingGear.IsLocked == true))
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Unlocking MotorStator: {MotorStatorsNearFarMap[motorStator].CustomName} due to locked landing gear!\n", true, false);
                            }

                            // Iterate through the landing gears and make sure autolock is on!
                            foreach (IMyLandingGear landingGear in FarMotorToLandingGearMap[MotorStatorsNearFarMap[motorStator]])
                            {
                                landingGear.AutoLock = false;
                            }

                            if (VerboseMode)
                            {
                                ReportError($"AutoLocked Disabled for {FarMotorToLandingGearMap.Count} IMyLandingGear!\n", true, false);
                            }

                            try
                            {
                                switch (this.MotorStatorDiscriminator)
                                {
                                    case MotorStatorTypeEnum.Hinge:

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].Reset();
                                        }
                                        break;

                                    case MotorStatorTypeEnum.AdvancedRotor:

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].Reset();
                                        }
                                        break;

                                    case MotorStatorTypeEnum.Rotor:

                                        if (!MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].MoveNext())
                                        {
                                            MotorStatorToStowMovement2DEnumMap[MotorStatorsNearFarMap[motorStator]].Reset();
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
                        }
                    }

                }
                catch (Exception ex)
                {
                    ReportError($"Error in UnStowGuns while attempting to iterate MotorToLandingGearMap!\n{ex}", true, false);
                }

            }
            catch (Exception ex)
            {
                ReportError($"Error in UnStowGuns!\n{ex}", true, false);
            }

        }


        // Hinge Stow movement will move the hinge to the desiredAngle, and if it stops or hits an obstruction - it will reverse.
        // Returns 'false' until it reaches desired angle.
        public IEnumerator<bool> StowMovementEnumerator(IMyMotorStator motorStator, float desiredAngle, float velocity, bool reverseAfterStop = false)
        {
            Dictionary<IMyMotorStator, float> MotorStatorToLastAngleMap = new Dictionary<IMyMotorStator, float>();
            float lastCurrentAngleDiff = 0;

            while (true)
            {
                try
                {
                    lastCurrentAngleDiff = Math.Abs(MotorStatorToLastAngleMap[motorStator] - motorStator.Angle);

                    // Check if the rotor might be stuck (compare against the previous angle)
                    if (MotorStatorToLastAngleMap.ContainsKey(motorStator) && (lastCurrentAngleDiff < 0.01))
                    {
                        if (reverseAfterStop)
                        {

                            if (VerboseMode)
                            {
                                ReportError($"Hit STOW REVERSE condition at {DateTime.Now.ToLongTimeString()}\n" +
                                    $"AngleActual:{motorStator.Angle}\n" +
                                    $"Last Angle:{MotorStatorToLastAngleMap[motorStator]}\n", false, false);
                            }

                            // This changes the desiredAngle to be the opposite of the current angle. AKA: "Reverse"
                            desiredAngle = -1 * desiredAngle;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"Error in HingeStowMovementEnumerator -> Checking Reverse Condition!\n{ex}");
                }


                // Next: Check if that angle is the desired angle.
                if (motorStator.Angle != desiredAngle)
                {
                    motorStator.RotorLock = false;

                    motorStator.RotateToAngle(MyRotationDirection.AUTO, desiredAngle, velocity);

                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        Echo("HingeStowMovementEnumerator returning true for:\n" +
                                "-----\n" +
                                $"{motorStator.CustomName}\n" +
                                $"angle: {motorStator.Angle}\n" +
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
                            $"angle: {motorStator.Angle}\n" +
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
                        MotorStatorToLastAngleMap.Add(motorStator, motorStator.Angle);
                    }
                    else
                    {
                        // Otherwise: Assign the current angle to the "Last Angle Map"
                        MotorStatorToLastAngleMap[motorStator] = motorStator.Angle;
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
                if (motorStator.Angle != 0)
                {
                    motorStator.RotorLock = false;

                    motorStator.RotateToAngle(MyRotationDirection.AUTO, 0, velocity);

                    if (VerboseMode && (MotorStatorToLastAngleMap.ContainsKey(motorStator)))
                    {
                        if (VerboseMode)
                        {
                            ReportError("HingeStowMovementEnumerator returning true for:\n" +
                                    "-----\n" +
                                    $"{motorStator.CustomName}\n" +
                                    $"angle: {motorStator.Angle}\n" +
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
                        ReportError("HingeStowMovementEnumerator returning false for:\n" +
                            "-----\n" +
                            $"{motorStator.CustomName}\n" +
                            $"angle: {motorStator.Angle}\n" +
                            $"last angle: {MotorStatorToLastAngleMap[motorStator]}\n" +
                            $"desiredAngle: {0}\n" +
                            $"velocity: {velocity}\n" +
                            $"reverseAfterStop: {reverseAfterStop}\n" +
                            $"Rotation STOPPED!\n" +
                            $"-----\n", false, false);
                    }

                    motorStator.RotorLock = true;
                    yield return false;
                }

                try
                {
                    // Update the last motorStator angle with the current angle
                    if (!MotorStatorToLastAngleMap.ContainsKey(motorStator))
                    {
                        MotorStatorToLastAngleMap.Add(motorStator, motorStator.Angle);
                    }
                    else
                    {
                        // Otherwise: Assign the current angle to the "Last Angle Map"
                        MotorStatorToLastAngleMap[motorStator] = motorStator.Angle;
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
                    GridTerminalSystem.GetBlocksOfType<IMyTurretControlBlock>(this.TurretControllers, (ctcBlock) => ctcBlock.IsSameConstructAs(Me));
                }

                foreach (IMyTurretControlBlock ctc in this.TurretControllers)
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
            catch (Exception ex)
            {
                ReportError($"Error in ResetHingeTopGridCameraNames!\n{ex}", true, true);
            }
        }

        private void StandbyForWC()
        {
            while (!WCAPI.Activate(Me))
            {
                WCAPI.Activate(Me);
            }
        }

        private void AssignMotorStatorOptions(IMyMotorStator motorStator)
        {
            // Assign Stator-Specific options here
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
                ctc.AngleDeviation = 1f;
                ctc.VelocityMultiplierAzimuthRpm = 30f;
                ctc.VelocityMultiplierElevationRpm = 30f;
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

        public void AssignCTCCamerasLoop(bool assignCameras, int i)
        {
            try
            {
                // Find a camera on the subgrid associated with the IMotorStator, and assign it to the CTC (if working)
                //CameraListIter = new List<IMyCameraBlock>();

                // Populate the list of cameras on the subgrid
                //GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (camera) => (camera.CubeGrid.IsSameConstructAs(CTCToMotorMap.ElementAt(i).Value.TopGrid)));

                // Iterate through the camera blocks and append a tag to identify which IMyTurretControlBlock/IMyMotorStator combination they are associated with
                for (int j = 0; j < MotorToCameraMap[NearCTCToMotorMap.ElementAt(i).Value].Count; j++)
                {
                    // Echo($"Found Camera {CameraListIter.ElementAt(j).CustomName}, associating with {CTCToMotorDict.ElementAt(i).Key.CustomName}");
                    if (VerboseMode)
                    {
                        ReportError($"Entering CTCSetupStage2 -> Camera Naming and Assignment Loop Iter {i}, {j}!\n", true, false);
                    }

                    MotorToCameraMap[NearCTCToMotorMap.ElementAt(i).Value].ElementAt(j).CustomName = $"{CameraTag}-{i}-{j}";

                    // We'll just iterate-and-assign each Camera to the CTC as we find it. This will have the effect of ultimately assigning the 'last' camera to the CTC, but that shouldn't be an issue...

                    if (assignCameras)
                    {
                        NearCTCToMotorMap.ElementAt(i).Key.Camera = CameraListIter.ElementAt(j);
                    }
                    else
                    {
                        NearCTCToMotorMap.ElementAt(i).Key.Camera = null;
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError($"Error in CTCSetupStage2 -> Camera Naming and Assignment!\n" +
                    $"MotorToCameraMap Count: {MotorToCameraMap.Count}\n" +
                    $"CurrentElement:{NearCTCToMotorMap.ElementAt(i)}\n" +
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
                for (int i = 0; i < TurretControllers.Count; i++)
                {
                    //Echo($"Found Controller {i} with CustomName {TurretControllerList[i].CustomName}");

                    // We use '-' characters to designate a controller, and will use the same integer (ex: [PICCTC-1]) to associate pieces of equipment [PICMOTOR-1].
                    if (TurretControllers[i].CustomName.Contains(CTCTag + "-"))
                    {
                        TurretControllers[i].CustomName = "Custom Turret Controller " + CTCTag;
                    }

                    TurretControllers[i].CustomName = autoAssign ? TurretControllers[i].DefinitionDisplayNameText + $" {CTCTag}-{i}" : TurretControllers[i].CustomName.Replace(CTCTag, $"{CTCTag}-{i}");
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

                } catch (Exception ex)
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
                } catch (Exception ex)
                {
                    ReportError($"Error when assigning Far MotorStator names!\n{ex}", true, false);
                }

            }
        }

        public void AssociateCTCsAndMotorStators()
        {
            // Iterate through the length of the MotorStatorList and each CTC to a MotorStator with a tag with a matching number
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    switch (MotorStatorDimensions)
                    {
                        case MotorStatorDimensionEnum.Azimuth:
                            NearCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNear[i]);

                            break;
                        case MotorStatorDimensionEnum.Elevation:
                            NearCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNear[i]);
                            break;

                        case MotorStatorDimensionEnum.ElevationAndAzimuth:
                            NearCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNear[i]);
                            FarCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNearFarMap[MotorStatorsNear[i]]);
                            break;

                        case MotorStatorDimensionEnum.AzimuthAndElevation:
                            NearCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNear[i]);
                            FarCTCToMotorMap.Add(TurretControllers[i], MotorStatorsNearFarMap[MotorStatorsNear[i]]);
                            break;

                    }
                }
                catch (Exception e)
                {
                    ReportError($"Error pairing CTCs to MotorStators!\n{e}", true, false);
                    //Echo($"Error pairing CTCs to MotorStators!\n{e}");
                }
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
                    nearStator.TopGrid.EntityId == farStatorCandidate.CubeGrid.EntityId &&
                    nearStator.IsWorking &&
                    farStatorCandidate.IsWorking
                    );

                    if(farStatorListIter.Count > 0)
                    {
                        if (VerboseMode)
                        {
                            ReportError($"Found {farStatorListIter.Count} far stators associated with near stator {nearStator.CustomName}\n", true, false);
                        }

                        this.MotorStatorsNearFarMap.Add(nearStator, farStatorListIter.First());
                    } else
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

            } catch (Exception ex)
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

                    GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (cameraBlock) => cameraBlock.CubeGrid.EntityId == (MotorStatorsNear[i].TopGrid.EntityId));

                    if (VerboseMode)
                    {
                        ReportError($"AssociateMotorStatorsAndCameras: Associating list of {CameraListIter.Count} NEAR entries with {MotorStatorsNear[i].CustomName}\n", true, false);
                    }

                    if(CameraListIter.Count == 0)
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
                        GridTerminalSystem.GetBlocksOfType<IMyCameraBlock>(CameraListIter, (cameraBlock) => cameraBlock.CubeGrid.EntityId == (MotorStatorsNearFarMap[MotorStatorsNear[i]].TopGrid.EntityId));

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
                    ReportError($"Error in AssociateMotorStatorsAndCameras!\n{e}", true, true);
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
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));
                        break;

                    case MotorStatorDimensionEnum.Elevation:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));
                        break;

                    case MotorStatorDimensionEnum.AzimuthAndElevation:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));

                        MotorStatorToStowMovement2DEnumMap.Add(motorStator, StowMovementEnumerator(MotorStatorsNearFarMap[motorStator], StowArc, 5, true));
                        MotorStatorToUnStowMovement2DEnumMap.Add(motorStator, UnStowMovementEnumerator(MotorStatorsNearFarMap[motorStator], 5, true));
                        break;

                    case MotorStatorDimensionEnum.ElevationAndAzimuth:
                        MotorStatorToStowMovement1DEnumMap.Add(motorStator, StowMovementEnumerator(motorStator, StowArc, 5, true));
                        MotorStatorToUnStowMovement1DEnumMap.Add(motorStator, UnStowMovementEnumerator(motorStator, 5, true));

                        MotorStatorToStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], StowMovementEnumerator(MotorStatorsNearFarMap[motorStator], StowArc, 5, true));
                        MotorStatorToUnStowMovement2DEnumMap.Add(MotorStatorsNearFarMap[motorStator], UnStowMovementEnumerator(MotorStatorsNearFarMap[motorStator], 5, true));
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
            // Iterate through the length of the MotorStatorList and each CTC to a MotorStator with a tag with a matching number
            for (int i = 0; i < MotorStatorsNear.Count; i++)
            {
                try
                {
                    NearMotorStatorLandingGearListIter = new List<IMyLandingGear>();
                    FarMotorStatorLandingGearListIter = new List<IMyLandingGear>();

                    GridTerminalSystem.GetBlocksOfType<IMyLandingGear>(NearMotorStatorLandingGearListIter, (landingGear) => landingGear.CubeGrid.EntityId == (MotorStatorsNear[i].TopGrid.EntityId));
                    GridTerminalSystem.GetBlocksOfType<IMyLandingGear>(FarMotorStatorLandingGearListIter, (landingGear) => landingGear.CubeGrid.EntityId == (MotorStatorsNearFarMap[MotorStatorsNear[i]].TopGrid.EntityId));

                    if (VerboseMode)
                    {
                        ReportError($"BEGIN: AssociateMotorStatorsAndLandingGears\n", true, false);

                        ReportError($"AssociateMotorStatorsAndLandingGears: Found {NearMotorStatorLandingGearListIter.Count} NEAR Landing Gears!\n" +
                                    $"AssociateMotorStatorsAndLandingGears: Found {FarMotorStatorLandingGearListIter.Count} FAR Landing Gears!\n" +
                                    $"MotorStatorsNearFarMap has {MotorStatorsNearFarMap.Count} entries.\n" +
                                    $"MotorStatorsNear has {MotorStatorsNear.Count} entries.\n", true, false);
                    }

                    foreach (IMyLandingGear landingGear in NearMotorStatorLandingGearListIter)
                    {
                        ReportError($"AssociateMotorStatorsAndLandingGears Iterating NearMotorStatorLandingGearListIter: {landingGear.CustomName}\n", true, false);

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
                        ReportError($"AssociateMotorStatorsAndLandingGears Iterating FarMotorStatorLandingGearListIter: {landingGear.CustomName}\n", true, false);

                        if (FarMotorToLandingGearMap.ContainsKey(MotorStatorsNearFarMap[MotorStatorsNear[i]]))
                        {
                            FarMotorToLandingGearMap[MotorStatorsNearFarMap[MotorStatorsNear[i]]].Add(landingGear);
                        }
                        else
                        {
                            FarMotorToLandingGearMap.Add(MotorStatorsNearFarMap[MotorStatorsNear[i]], new List<IMyLandingGear> { landingGear });
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
                ReportError($"END: AssociateMotorStatorsAndLandingGears\n");
            }
        }

        public void CTCSetupStage2(bool assignCameras = true)
        {
            try
            {
                // Iterate through each IMyTurretControlBlock:IMyMotorStator pair and assign Rotors and Cameras as appropriate
                for (int i = 0; i < NearCTCToMotorMap.Count; i++)
                {
                    try
                    {
                        // Echo($"Attempting to assign MotorStators to CTC El/Az Rotors");
                        // Determine which Rotors (IMyMotorStators) to assign
                        switch (this.MotorStatorDimensions)
                        {
                            // Note: Oddly enough - When setting up a CTC turret that uses only a single dimension (Az or El): We are required to still set the CTC to use that single MotorStator as the Elevation AND Azimuth rotor.
                            case MotorStatorDimensionEnum.Azimuth:
                                NearCTCToMotorMap.ElementAt(i).Key.ElevationRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                NearCTCToMotorMap.ElementAt(i).Key.AzimuthRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                break;
                            case MotorStatorDimensionEnum.Elevation:
                                NearCTCToMotorMap.ElementAt(i).Key.ElevationRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                NearCTCToMotorMap.ElementAt(i).Key.AzimuthRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                break;
                            case MotorStatorDimensionEnum.AzimuthAndElevation:
                                NearCTCToMotorMap.ElementAt(i).Key.AzimuthRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                FarCTCToMotorMap.ElementAt(i).Key.ElevationRotor = FarCTCToMotorMap[NearCTCToMotorMap.ElementAt(i).Key];
                                break;
                            case MotorStatorDimensionEnum.ElevationAndAzimuth:
                                NearCTCToMotorMap.ElementAt(i).Key.ElevationRotor = NearCTCToMotorMap.ElementAt(i).Value;
                                FarCTCToMotorMap.ElementAt(i).Key.AzimuthRotor = FarCTCToMotorMap[NearCTCToMotorMap.ElementAt(i).Key];
                                break;
                        }
                    } catch (Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> Rotor Assignment!\n{ex}", true, false);
                    }


                    try
                    {
                        AssignCTCOptions(NearCTCToMotorMap.ElementAt(i).Key);
                    } catch(Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> AssignCTCOptions!\n{ex}", true, false);
                    }

                    try
                    {
                        AssignMotorStatorOptions(NearCTCToMotorMap.ElementAt(i).Value);
                    }
                    catch (Exception ex)
                    {
                        ReportError($"Error in CTCSetupStage2 -> AssignMotorStatorOptions!\n{ex}", true, false);
                    }

                    AssignCTCCamerasLoop(assignCameras, i);
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
            TurretControllers = new List<IMyTurretControlBlock>();
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

            // Populate Lists of trivially-obtainable blocks
            GridTerminalSystem.GetBlocksOfType<IMyTurretControlBlock>(TurretControllers, (ctcBlock) => autoAssign ? ctcBlock.IsSameConstructAs(Me) : ctcBlock.CustomName.Contains(CTCTag) && ctcBlock.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType<IMyMotorStator>(MotorStatorsNear, (motorBlock) => autoAssign ? motorBlock.CubeGrid.EntityId == Me.CubeGrid.EntityId : motorBlock.CustomName.Contains(MotorStatorTag) && motorBlock.CubeGrid.EntityId == Me.CubeGrid.EntityId);
            GridTerminalSystem.GetBlocksOfType<IMyShipMergeBlock>(MergeBlocks, (mergeBlock) => autoAssign ? mergeBlock.IsSameConstructAs(Me) : mergeBlock.CustomName.Contains(MotorStatorTag) && mergeBlock.CustomName.Contains(MergeBlockTag) && mergeBlock.IsSameConstructAs(Me));

            if (VerboseMode)
            {
                ReportError($"List Counts:\n" +
                    $"TurretControllers: {TurretControllers.Count}\n" +
                    $"MotorStators: {MotorStatorsNear.Count}\n" +
                    $"MergeBlocks: {MergeBlocks.Count}\n",
                    true, false);
            }

            // This guard is important!
            // This script relies on many assumptions.
            // An assumption that you have at one TurretController for each MotorStator.
            // You can have more TurretControllers, but we are assuming that setup will be ran on a meme-CTC grid.
            // TurretControllers >= MotorStators (Rotors,Adv. Rotors, Hinges)
            if (TurretControllers.Count < MotorStatorsNear.Count)
            {
                ReportError($"Error in CTCSetupStage1:\nInvalid ratio of CTC's ({TurretControllers.Count}) to MotorStators ({MotorStatorsNear.Count})!\nNeed to have at least one CTC per MotorStator (Hinge, Rotor, etc)!", true, false);
                // Echo($"Error in CTCSetupStage1: Invalid ratio of CTC's ({TurretControllers.Count}) to MotorStators ({MotorStators.Count})!\nNeed to have at least one CTC per MotorStator (Hinge, Rotor, etc)!");
                return;
            }


            try
            {
                AssignCTCNames(true);

                AssociateNearFarMotorStators();

                AssignMotorStatorNames(true);

                AssociateCTCsAndMotorStators();

                AssociateMotorStatorsAndLandingGears();

                AssociateMotorStatorsAndCameras();

                AssociateMotorStatorsStwMvmntEnumerators();

                CTCSetupStage2(false);
            }
            catch (Exception ex)
            {
                ReportError($"CTCSetupStage1: General Exception Throw!\n{ex}\n", true, true);
            }

        }
    }
}
