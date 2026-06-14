using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppRUMBLE.Managers;
using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Tutorial.MoveLearning;
using MelonLoader;
using RumbleModdingAPI.RMAPI;
using UIFramework;
using UnityEngine;
using Valve.VR;
using AudioManager = Il2CppRUMBLE.Managers.AudioManager;
using Main = FBTMod.Main;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(Main), "FBTMod", "1.0.0", "ERROR")]
[assembly: MelonGame("Buckethead Entertainment", "RUMBLE")]
[assembly: MelonAdditionalDependencies("UIFramework")]

namespace FBTMod
{
    public class Main : MelonMod
    {
        public Main instance;
        public Main() => instance = this;

        public static Player LocalPlayer => PlayerManager.instance.LocalPlayer;
        public static Transform referenceSkeleton;
        
        public CVRSystem vrSystem;

        private LegIKSolver leftLegSolver;
        private LegIKSolver rightLegSolver;

        private bool isCalibrated;
        private bool isCalibrating;
        
        // Trackers
        public class TrackerState
        {
            public uint DeviceIndex;
            public Vector3 position;
            public Quaternion rotation;
            public bool IsConnected;
        }

        // Supported trackers
        public enum TrackerRole
        {
            Unassigned,
            Chest,
            Hips,
            LeftKnee,
            RightKnee,
            LeftFoot,
            RightFoot
        }
        
        // Offsets
        public class TrackerCalibration
        {
            public uint DeviceIndex;
            public Pose offset;
        }

        public class Pose
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        // OpenVR device indexes are in a fixed range of 64
        private TrackedDevicePose_t[] poses = new TrackedDevicePose_t[64];
        
        private Dictionary<uint, TrackerState> trackers = new();
        private GameObject[] debugSpheres = new GameObject[64];
        
        private Dictionary<TrackerRole, TrackerCalibration> trackerOffsets = new();
        private Dictionary<TrackerRole, Pose> runtimeTrackerTransforms = new();
        
        // Custom Poses
        public class FootPoseDefinition
        {
            public Vector3 LeftPosition;
            public Quaternion LeftRotation;

            public Vector3 RightPosition;
            public Quaternion RightRotation;

            public float PositionMargin = 0.25f;
            public float RotationMargin = 45f;
        }

        public class FootPoseSequence
        {
            public string Name;
            public List<FootPoseDefinition> Steps = new();

            public int CurrentStep;
            public float LastStepTime;
            public float MaxTimeBetweenSteps = 1.5f;

            public float LastTriggerTime;
            public float Cooldown = 0.25f;

            public Action onStepCompleted;
            public Action onSequenceCompleted;
            public Action onSequenceFailed;

            public bool WasInsideCurrentStep;
        }

        private List<FootPoseSequence> customPoses = new();
        private List<FootPoseDefinition> currentRecordedPose = new();
        
        // SETTINGS
        private MelonPreferences_Category Settings;

        private MelonPreferences_Entry NewMoveName;

        private const int LEG_SOLVE_ITERATIONS = 3;
        
        private const float HIP_POSITION_WEIGHT = 1f;
        private const float HIP_ROTATION_WEIGHT = 1f;
        
        private const float CHEST_POSITION_WEIGHT = 1f;
        private const float CHEST_ROTATION_WEIGHT = 1f;

        private const string USER_DATA = "UserData/FullBodyTracking";
        private const string CONFIG_FILE = "config.cfg";
        
        // ----------------------------------------------------------------
        
        public override void OnLateInitializeMelon() =>
            Actions.onMapInitialized += _ => OnMapInitialized();

        public override void OnInitializeMelon()
        {
            // Doesn't create it if it already exists
            Directory.CreateDirectory(USER_DATA);
            
            Settings = MelonPreferences.CreateCategory("FBTMod_Settings", "Full-Body Tracking");
            Settings.SetFilePath(Path.Combine(USER_DATA, CONFIG_FILE));
            
            UI.CreateButtonEntry(Settings, "Calibrate", "Calibrate", 
                "Starts FBT Calibration. Line yourself up with the shown pose, then pres both triggers to confirm.",
                () =>
                {
                    if (!isCalibrating)
                        MelonCoroutines.Start(Calibration());
                }
            );

            NewMoveName = Settings.CreateEntry("FBT_NewMoveName", "Straight", "New Move Name", "An existing rumble move name for your sequence to activate.");
            
            UI.CreateButtonEntry(Settings, "Add Current Pose", "Add Current Pose",
                "Adds the current pose of your feet to the sequence.",
                () =>
                {
                    if (!isCalibrated)
                    {
                        LoggerInstance.Warning("Cannot save foot pose before FBT calibration.");
                        return;
                    }
            
                    currentRecordedPose.Add(CreateFootPoseFromCurrentFeet());
                });
            
            UI.CreateButtonEntry(Settings, "Save Current Sequence", "Save Current Sequence",
                "Saves the current sequence of poses to allow you to hit the custom sequence. Clears stored sequence.",
                () =>
                {
                    if (currentRecordedPose.Count > 0)
                    {
                        var sequence = new FootPoseSequence
                        {
                            Name = NewMoveName.BoxedEditedValue.ToString(),
                            Steps = currentRecordedPose.ToList(),
                            onStepCompleted = () =>
                            {
                                LoggerInstance.Msg("Triggered foot step");
            
                                var poseHitAudioCall = GameObjects.Gym.INTERACTABLES.PoseGhost.Ghost.GetGameObject().GetComponent<PoseGhost>().moveSuccessSFX;
                                AudioManager.instance.Play(poseHitAudioCall, LocalPlayer.Controller.PlayerVR.transform.position);
                            },
                            
                            onSequenceFailed = () =>
                            {
                                LoggerInstance.Msg("Foot step failed");
            
                                var poseHitAudioCall = GameObjects.Gym.INTERACTABLES.PoseGhost.Ghost.GetGameObject().GetComponent<PoseGhost>().moveTooLateSuccessSFX;
                                AudioManager.instance.Play(poseHitAudioCall, LocalPlayer.Controller.PlayerVR.transform.position);
                            },
                            
                            onSequenceCompleted = () =>
                            {
                                LoggerInstance.Msg("Foot sequence completed.");
                                
                                var straightStack = LocalPlayer.Controller.PlayerProcessor.availableStacks.ToArray().FirstOrDefault(s => s.name == NewMoveName.BoxedEditedValue.ToString());
            
                                if (straightStack == null)
                                {
                                    LoggerInstance.Warning($"No move with the name {NewMoveName.BoxedEditedValue} could be found. Did you type a matching rumble stack name?");
                                    return;
                                }
            
                                LocalPlayer.Controller.PlayerProcessor.Execute(straightStack);
                            }
                        };
                        
                        customPoses.Add(sequence);
                        currentRecordedPose.Clear();
                    }
                });
            
            UI.CreateButtonEntry(Settings, "Clear Poses", "Clear Poses",
                "Clears all custom poses.",
                () =>
                {
                    customPoses.Clear();
                });

            UI.RegisterMelon(this, Settings);

            EVRInitError error = EVRInitError.None;
            vrSystem = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Other);

            if (error != EVRInitError.None)
            {
                LoggerInstance.Error($"[FBT] OpenVR initialization failed: {error}");
                vrSystem = null;
                return;
            }

            LoggerInstance.Msg("[FBT] OpenVR initialized.");
        }

        public override void OnApplicationQuit()
        {
            if (vrSystem != null)
            {
                OpenVR.Shutdown();
                vrSystem = null;
            }
        }

        private void OnMapInitialized()
        {
            EnsureStaticObjects();

            trackerOffsets.Clear();
            runtimeTrackerTransforms.Clear();

            for (int i = 0; i < debugSpheres.Length; i++)
            {
                GameObject trackerSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                trackerSphere.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                Object.Destroy(trackerSphere.GetComponent<Collider>());
                trackerSphere.transform.localScale = Vector3.one * 0.2f;
                debugSpheres[i] = trackerSphere;
            }

            isCalibrating = false;
            isCalibrated = false;

            leftLegSolver = null;
            rightLegSolver = null;
        }

        public static void EnsureStaticObjects()
        {
            if (referenceSkeleton != null)
                return;

            PlayerController templateController = Resources
                .FindObjectsOfTypeAll<PlayerController>()
                .FirstOrDefault(p => !p.gameObject.scene.IsValid());

            Transform visuals = templateController.PlayerVisuals.transform;

            referenceSkeleton = GameObject.Instantiate(visuals.GetChild(1).gameObject).transform;
            referenceSkeleton.name = "FBT_ReferenceSkeleton";
            Object.DontDestroyOnLoad(referenceSkeleton.gameObject);
        }

        // T-Pose
        public static void ToggleTPose(PlayerController target, bool toggle)
        {
            target.PlayerIK.enabled = !toggle;
            target.PlayerIK.VrIK.enabled = !toggle;

            var animator = target.PlayerAnimator.animator;
            animator.enabled = !toggle;

            if (toggle && referenceSkeleton != null)
            {
                Transform playerSkeleton = animator.transform.GetChild(1);
                CopySkeleton(playerSkeleton, referenceSkeleton);
            }
        }

        public static void CopySkeleton(Transform target, Transform source)
        {
            target.localRotation = source.localRotation;

            for (int i = 0; i < target.childCount; i++)
            {
                Transform targetChild = target.GetChild(i);
                Transform sourceChild = source.Find(targetChild.name);

                if (sourceChild != null)
                    CopySkeleton(targetChild, sourceChild);
            }
        }
        
        // ----------------------------------------------------------------

        private static Quaternion ToLocalRotation(Transform root, Quaternion worldRot)
        {
            return Quaternion.Inverse(root.rotation) * worldRot;
        }

        private static Quaternion ToWorldRotation(Transform root, Quaternion localRot)
        {
            return root.rotation * localRot;
        }
        
        // ----------------------------------------------------------------

        private bool CreateLegSolvers()
        {
            Animator animator = LocalPlayer.Controller.PlayerAnimator.animator;
            
            Transform leftUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform leftLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            
            Transform rightUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            Transform rightLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

            if (!runtimeTrackerTransforms.TryGetValue(TrackerRole.LeftFoot, out var leftFootTarget) ||
                !runtimeTrackerTransforms.TryGetValue(TrackerRole.RightFoot, out var rightFootTarget) ||
                !runtimeTrackerTransforms.TryGetValue(TrackerRole.LeftKnee, out var leftKneeHint) ||
                !runtimeTrackerTransforms.TryGetValue(TrackerRole.RightKnee, out var rightKneeHint))
            {
                LoggerInstance.Error("Could not create custom leg solvers. Missing FBT targets.");
                return false;
            }

            Vector3 defaultBendDir = animator.transform.forward;
            
            leftLegSolver = new LegIKSolver(
                leftUpperLeg,
                leftLowerLeg,
                leftFoot,
                leftFootTarget,
                leftKneeHint,
                defaultBendDir
            );

            rightLegSolver = new LegIKSolver(
                rightUpperLeg,
                rightLowerLeg,
                rightFoot,
                rightFootTarget,
                rightKneeHint,
                defaultBendDir
            );

            leftLegSolver.Weight = 1f;
            rightLegSolver.Weight = 1f;

            LoggerInstance.Msg("Leg solvers created.");
            return true;
        }

        private void ToggleVrikLegSolving(bool toggle)
        {
            var ik = LocalPlayer.Controller.PlayerIK.VrIK;
            var value = toggle ? 1f : 0f;
            
            ik.solver.leftLeg.positionWeight = value;
            ik.solver.rightLeg.positionWeight = value;
            
            ik.solver.leftLeg.rotationWeight = value;
            ik.solver.rightLeg.rotationWeight = value;
            
            ik.solver.leftLeg.bendGoalWeight = value;
            ik.solver.rightLeg.bendGoalWeight = value;
        }
        
        // Builds target calibration points based on the current pose of the player (should be T-Pose when ran)
        private void CreateCalibrationTargets()
        {
            Animator animator = LocalPlayer.Controller.PlayerAnimator.animator;

            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            
            Transform leftLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            Transform rightLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

            Vector3 bendForward = animator.transform.forward;

            Pose hipTarget = new Pose();
            hipTarget.position = hips.position;
            hipTarget.rotation = hips.rotation;

            Pose chestTarget = new Pose();
            chestTarget.position = chest.position;
            chestTarget.rotation = chest.rotation;
            
            Pose leftKneeTarget = new Pose();
            leftKneeTarget.position = leftLowerLeg.position + bendForward * 0.05f;
            leftKneeTarget.rotation = leftLowerLeg.rotation;
            
            Pose rightKneeTarget = new Pose();
            rightKneeTarget.position = rightLowerLeg.position + bendForward * 0.05f;
            rightKneeTarget.rotation = rightLowerLeg.rotation;
            
            Pose leftFootTarget = new Pose();
            leftFootTarget.position = leftFoot.position;
            leftFootTarget.rotation = leftFoot.rotation;
            
            Pose rightFootTarget = new Pose();
            rightFootTarget.position = rightFoot.position;
            rightFootTarget.rotation = rightFoot.rotation;
            
            runtimeTrackerTransforms = new Dictionary<TrackerRole, Pose>()
            {
                { TrackerRole.Chest, chestTarget },
                { TrackerRole.Hips, hipTarget },
                { TrackerRole.LeftFoot, leftFootTarget },
                { TrackerRole.RightFoot, rightFootTarget },
                { TrackerRole.LeftKnee, leftKneeTarget },
                { TrackerRole.RightKnee, rightKneeTarget }
            };
        }
        
        private bool AssignNearestTrackers()
        {
            trackerOffsets.Clear();

            Transform root = LocalPlayer.Controller.PlayerVR.transform;
            List<uint> available = trackers.Keys.ToList();
            
            if (available.Count < 6)
            {
                LoggerInstance.Error($"Not enough trackers for full calibration. Found {trackers.Count}, expected 6.");
                return false;
            }

            foreach (var pair in runtimeTrackerTransforms)
            {
                TrackerRole role = pair.Key;
                Pose target = pair.Value;

                uint bestIndex = 0;
                float bestDistance = float.MaxValue;
                bool found = false;

                foreach (uint index in available)
                {
                    float dist = Vector3.Distance(trackers[index].position, target.position);

                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        bestIndex = index;
                        found = true;
                    }
                }

                if (!found)
                {
                    LoggerInstance.Warning($"Could not find tracker for {role}.");
                    continue;
                }

                available.Remove(bestIndex);

                Vector3 localTrackerPos = root.InverseTransformPoint(trackers[bestIndex].position);
                Vector3 localTargetPos = root.InverseTransformPoint(target.position);

                Quaternion localTrackerRot = ToLocalRotation(root, trackers[bestIndex].rotation);
                Quaternion localTargetRot = ToLocalRotation(root, target.rotation);
                
                trackerOffsets[role] = new TrackerCalibration
                {
                    DeviceIndex = bestIndex,
                    offset = new Pose
                    {
                        position = localTargetPos - localTrackerPos,
                        rotation = Quaternion.Inverse(localTrackerRot) * localTargetRot
                    }
                };
                
                LoggerInstance.Msg($"{role} assigned to tracker {bestIndex}, distance {bestDistance:F3}");
            }

            bool hasMinimum =
                trackerOffsets.ContainsKey(TrackerRole.Hips) &&
                trackerOffsets.ContainsKey(TrackerRole.Chest) &&
                trackerOffsets.ContainsKey(TrackerRole.LeftFoot) &&
                trackerOffsets.ContainsKey(TrackerRole.RightFoot) &&
                trackerOffsets.ContainsKey(TrackerRole.LeftKnee) &&
                trackerOffsets.ContainsKey(TrackerRole.RightKnee);

            if (!hasMinimum)
            {
                LoggerInstance.Error("Calibration failed: missing required trackers.");
                return false;
            }

            return true;
        }
        
        private IEnumerator Calibration()
        {
            static bool AreBothTriggersPressed()
            {
                return Calls.ControllerMap.LeftController.GetTrigger() > 0.75f &&
                       Calls.ControllerMap.RightController.GetTrigger() > 0.75f;
            }
            
            isCalibrating = true;
            isCalibrated = false;

            EnsureStaticObjects();
            
            ToggleTPose(LocalPlayer.Controller, true);

            yield return null;

            while (AreBothTriggersPressed())
                yield return null;

            while (!AreBothTriggersPressed())
                yield return null;
            
            // Player is (hopefully) matching T-Pose, calibrate.

           CreateCalibrationTargets();
            
            if (!AssignNearestTrackers())
            {
                ToggleTPose(LocalPlayer.Controller, false);
                isCalibrating = false;
                yield break;
            }

            if (!CreateLegSolvers())
            {
                ToggleTPose(LocalPlayer.Controller, false);
                isCalibrating = false;
                yield break;
            }

            ToggleTPose(LocalPlayer.Controller, false);

            ToggleVrikLegSolving(false);

            isCalibrating = false;
            isCalibrated = true;
        }
        
        // ----------------------------------------------------------------
        // Custom Poses

        private FootPoseDefinition CreateFootPoseFromCurrentFeet()
        {
            Transform root = LocalPlayer.Controller.PlayerVR.transform;

            Pose leftFoot = runtimeTrackerTransforms[TrackerRole.LeftFoot];
            Pose rightFoot = runtimeTrackerTransforms[TrackerRole.RightFoot];
            
            return new FootPoseDefinition
            {
                LeftPosition = root.InverseTransformPoint(leftFoot.position),
                LeftRotation = Quaternion.Inverse(root.rotation) * leftFoot.rotation,
                
                RightPosition = root.InverseTransformPoint(rightFoot.position),
                RightRotation = Quaternion.Inverse(root.rotation) * rightFoot.rotation,
                
                PositionMargin = 0.25f,
                RotationMargin = 45f
            };
        }

        private bool IsInsideFootPose(FootPoseDefinition pose)
        {
            Transform root = LocalPlayer.Controller.PlayerVR.transform;

            if (!runtimeTrackerTransforms.TryGetValue(TrackerRole.LeftFoot, out Pose leftFoot) ||
                !runtimeTrackerTransforms.TryGetValue(TrackerRole.RightFoot, out Pose rightFoot))
                return false;

            Vector3 leftPos = root.InverseTransformPoint(leftFoot.position);
            Quaternion leftRot = Quaternion.Inverse(root.rotation) * leftFoot.rotation;
            
            Vector3 rightPos = root.InverseTransformPoint(rightFoot.position);
            Quaternion rightRot = Quaternion.Inverse(root.rotation) * rightFoot.rotation;

            bool leftPositionOk =
                Vector3.Distance(leftPos, pose.LeftPosition) <= pose.PositionMargin;
            
            bool rightPositionOk =
                Vector3.Distance(rightPos, pose.RightPosition) <= pose.PositionMargin;

            bool leftRotationOk =
                Quaternion.Angle(leftRot, pose.LeftRotation) <= pose.RotationMargin;

            bool rightRotationOk =
                Quaternion.Angle(rightRot, pose.RightRotation) <= pose.RotationMargin;
            
            return leftPositionOk && rightPositionOk && leftRotationOk && rightRotationOk;
        }

        private void UpdateFootPoseSequence(FootPoseSequence sequence)
        {
            if (sequence == null || sequence.Steps.Count == 0)
                return;

            if (sequence.CurrentStep > 0 &&
                Time.time > sequence.LastStepTime + sequence.MaxTimeBetweenSteps)
            {
                ResetFootPoseSequence(sequence);
                sequence.onSequenceFailed?.Invoke();
                return;
            }

            FootPoseDefinition currentStep = sequence.Steps[sequence.CurrentStep];
            bool inside = IsInsideFootPose(currentStep);

            if (inside && !sequence.WasInsideCurrentStep)
            {
                sequence.LastStepTime = Time.time;
                sequence.CurrentStep++;
                sequence.WasInsideCurrentStep = true;
                sequence.onStepCompleted?.Invoke();

                if (sequence.CurrentStep >= sequence.Steps.Count)
                {
                    if (Time.time >= sequence.LastTriggerTime + sequence.Cooldown)
                    {
                        sequence.LastTriggerTime = Time.time;
                        sequence.onSequenceCompleted?.Invoke();
                    }
                    
                    ResetFootPoseSequence(sequence);
                    return;
                }

                sequence.WasInsideCurrentStep = IsInsideFootPose(sequence.Steps[sequence.CurrentStep]);
                return;
            }

            sequence.WasInsideCurrentStep = inside;
        }

        private void ResetFootPoseSequence(FootPoseSequence sequence)
        {
            sequence.CurrentStep = 0;
            sequence.LastStepTime = 0f;
                
            if (sequence.Steps.Count > 0)
                sequence.WasInsideCurrentStep = IsInsideFootPose(sequence.Steps[0]);
            else
                sequence.WasInsideCurrentStep = false;
        }
        
        // ----------------------------------------------------------------
        // Runtime

        private void UpdateRuntimeTrackers()
        {
            Transform root = LocalPlayer.Controller.PlayerVR.transform;

            for (int i = 0; i < trackerOffsets.Count; i++)
            {
                var (role, calibration) = trackerOffsets.ElementAt(i);
                
                if (!runtimeTrackerTransforms.TryGetValue(role, out var transform))
                    continue;

                if (!trackers.TryGetValue(calibration.DeviceIndex, out var state))
                    continue;
                
                Vector3 localTrackerPos = root.InverseTransformPoint(state.position);
                Vector3 correctedLocalPos = localTrackerPos + calibration.offset.position;

                transform.position = root.TransformPoint(correctedLocalPos);

                Quaternion localTrackerRot = ToLocalRotation(root, state.rotation);
                Quaternion correctedLocalRot = localTrackerRot * calibration.offset.rotation;

                transform.rotation = ToWorldRotation(root, correctedLocalRot);

                debugSpheres[i].transform.position = transform.position;
                debugSpheres[i].transform.rotation = transform.rotation;
            }
        }

        private void ApplyHipAndChestTracking()
        {
            if (!isCalibrated || isCalibrating)
                return;

            var animator = LocalPlayer.Controller.PlayerAnimator.animator;

            if (runtimeTrackerTransforms.TryGetValue(TrackerRole.Hips, out var transform))
            {
                var hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
                hipsBone.position = Vector3.Lerp(hipsBone.position, transform.position, HIP_POSITION_WEIGHT);
                hipsBone.rotation = Quaternion.Slerp(hipsBone.rotation, transform.rotation, HIP_ROTATION_WEIGHT);
            }

            if (runtimeTrackerTransforms.TryGetValue(TrackerRole.Chest, out transform))
            {
                var chestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
                chestBone.rotation = Quaternion.Slerp(chestBone.rotation, transform.rotation, CHEST_ROTATION_WEIGHT);
            }
        }
        
        public override void OnUpdate()
        {
            if (vrSystem == null || LocalPlayer?.Controller == null)
                return;
            
            vrSystem.GetDeviceToAbsoluteTrackingPose(
                ETrackingUniverseOrigin.TrackingUniverseStanding,
                0,
                poses
            );

            for (uint i = 0; i < poses.Length; i++)
            {
                bool connected = vrSystem.IsTrackedDeviceConnected(i);
                
                // Avoids the HMD/Controllers
                if (vrSystem.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker)
                    continue;
                
                TrackedDevicePose_t pose = poses[i];
                HmdMatrix34_t matrix = pose.mDeviceToAbsoluteTracking;

                // Flipped on the local Z-axis
                Vector3 position = new Vector3(
                    matrix.m3,
                    matrix.m7,
                    -matrix.m11
                );

                Vector3 forward = new Vector3(
                    -matrix.m2,
                    -matrix.m6,
                    matrix.m10
                );

                Vector3 up = new Vector3(
                    matrix.m1,
                    matrix.m5,
                    -matrix.m9
                );

                Quaternion rotation = Quaternion.LookRotation(forward, up);

                Transform root = LocalPlayer.Controller.PlayerVR.transform;

                position = root.TransformPoint(position);
                rotation = root.rotation * rotation;
                
                trackers[i] = new TrackerState
                {
                    DeviceIndex = i,
                    position = position,
                    rotation = rotation,
                    IsConnected = connected
                };
            }
            
            UpdateRuntimeTrackers();
        }

        public override void OnLateUpdate()
        {
            if (!isCalibrated || isCalibrating)
                return;

            UpdateRuntimeTrackers();
            ApplyHipAndChestTracking();

            foreach (var pose in customPoses)
            {
                if (pose != null)
                    UpdateFootPoseSequence(pose);
            }

            for (int i = 0; i < LEG_SOLVE_ITERATIONS; i++)
            {
                leftLegSolver?.Solve();
                rightLegSolver?.Solve();
            }
        }
    }
    
    public class LegIKSolver
    {
        private Transform UpperLeg;
        private Transform LowerLeg;
        private Transform Foot;

        private Main.Pose FootTarget;
        private Main.Pose KneeHint;

        public float Weight = 1f;
        
        private readonly float upperLen;
        private readonly float lowerLen;

        private Vector3 lastGoodBendDir;

        public LegIKSolver(
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Main.Pose footTarget,
            Main.Pose kneeHint,
            Vector3 defaultBendDir
        )
        {
            UpperLeg = upperLeg;
            LowerLeg = lowerLeg;
            Foot = foot;

            FootTarget = footTarget;
            KneeHint = kneeHint;

            upperLen = Vector3.Distance(UpperLeg.position, LowerLeg.position);
            lowerLen = Vector3.Distance(LowerLeg.position, foot.position);

            lastGoodBendDir = defaultBendDir.sqrMagnitude > 0.0001f
                ? defaultBendDir.normalized
                : Vector3.forward;
        }

        public void Solve()
        {
            if (UpperLeg == null || LowerLeg == null || Foot == null)
                return;

            Vector3 hipPos = UpperLeg.position;
            Vector3 targetFootPos = FootTarget.position;
            
            Vector3 hipToFoot = targetFootPos - hipPos;

            if (hipToFoot.sqrMagnitude < 0.0001f)
                return;

            float maxReach = upperLen + lowerLen - 0.001f;
            float minReach = Mathf.Abs(upperLen - lowerLen) + 0.001f;

            float rawDist = hipToFoot.magnitude;
            float dist = Mathf.Clamp(rawDist, minReach, maxReach);

            // Direction from the hip to the foot with distance removed.
            Vector3 legForward = hipToFoot.normalized;
            // Clamped foot tracker position based off avatar leg length
            Vector3 solvedFootPos = hipPos + legForward * dist;
            
            Vector3 hipToKneeHint = KneeHint.position - hipPos;
            // Flattens the vector to only tell which side of the hip-to-foot line it's on rather than the distance along it.
            Vector3 bendDir = Vector3.ProjectOnPlane(hipToKneeHint, legForward);

            // Corrections in case of odd positioning
            if (bendDir.sqrMagnitude < 0.0001f)
                bendDir = Vector3.ProjectOnPlane(lastGoodBendDir, legForward);
            
            if (bendDir.sqrMagnitude < 0.0001f)
                bendDir = Vector3.ProjectOnPlane(UpperLeg.forward, legForward);

            if (bendDir.sqrMagnitude < 0.0001f)
                return;

            bendDir.Normalize();

            // Sudden flip correction
            if (Vector3.Dot(bendDir, lastGoodBendDir) < -0.25f)
                bendDir = lastGoodBendDir;
            else
                lastGoodBendDir = bendDir;
            
            // Knee Position calculation
            float x = (dist * dist + upperLen * upperLen - lowerLen * lowerLen) / (2f * dist);
            float ySquared = upperLen * upperLen - x * x;
            float y = Mathf.Sqrt(Mathf.Max(0f, ySquared));

            Vector3 solvedKneePos = hipPos + legForward * x + bendDir * y;
            
            // The knee is a position with distance upper leg length from the hip, and lower leg length from the foot.
            // To solve for that, we find how far along the hip-to-foot line we have to go before the knee is perpendicular to this point.
            // Then, to solve for the y position, we have x^2 + y^2 = upperLen^2. We can solve for y to see how far the knee sticks out.
            
            // To find the position of the knee that fits within the length of the avatar's leg:
            // We start at the hip, and then move x along the hip-to-foot line. This puts us so the knee is perpendicular to this point.
            // Then, we move y in the knee bend direction, which is the distance perpendicular to the hip-to-foot line. This gives us the final
            // solved knee position.

            // Rotates the upper leg transform so the child joint, the knee, points toward the solved position
            RotateBoneToPoint(UpperLeg, LowerLeg.position, solvedKneePos, Weight);
            // Simply rotates the lower leg's end point to match the (clamped) foot tracker position.
            RotateBoneToPoint(LowerLeg, Foot.position, solvedFootPos, Weight);
        }

        private static void RotateBoneToPoint(Transform bone, Vector3 currentEndPos, Vector3 desiredEndPos, float weight)
        {
            Vector3 currentDir = currentEndPos - bone.position;
            Vector3 desiredDir = desiredEndPos - bone.position;

            if (currentDir.sqrMagnitude < 0.0001f || desiredDir.sqrMagnitude < 0.0001f)
                return;
            
            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            Quaternion targetRotation = delta * bone.rotation;

            bone.rotation = Quaternion.Slerp(bone.rotation, targetRotation, Mathf.Clamp01(weight));
        }
    }
}