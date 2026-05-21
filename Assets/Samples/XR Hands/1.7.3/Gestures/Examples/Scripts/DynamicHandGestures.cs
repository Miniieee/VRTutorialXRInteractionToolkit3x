using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

namespace UnityEngine.XR.Hands.Samples.GestureSample
{
    public class DynamicHandGestures : MonoBehaviour
    {
        private enum GestureState
        {
            WaitingForInitial,
            HoldingInitial,
            Armed,
            WaitingForTarget,
            WaitingForRecenterAfterSwipe
        }

        private enum SwipeDirection
        {
            None,
            Right,
            Left
        }

        private struct GestureRuntimeData
        {
            public float RawScore;
            public float SmoothedScore;
            public bool HasScore;
            public bool IsDetected;

            public void Reset()
            {
                RawScore = 0f;
                SmoothedScore = 0f;
                HasScore = false;
                IsDetected = false;
            }
        }

        [Header("Hand Tracking")]
        [SerializeField] private XRHandTrackingEvents handTrackingEvents;
        [SerializeField] private Handedness handedness = Handedness.Right;
        [SerializeField] private bool findHandTrackingEventsAtRuntime = true;
        [SerializeField] private float findRetryInterval = 0.25f;

        [Header("Initial Gesture")]
        [SerializeField] private HandShapeCompletenessCalculator completenessCalculator;
        [SerializeField] private ScriptableObject initialHandShapeOrPose;
        [SerializeField] private Transform initialTargetTransform;
        [SerializeField] private float initialHoldTime = 0.15f;

        [Header("Initial Gesture Detection")]
        [SerializeField, Range(0f, 1f)] private float shapeEnterThreshold = 0.85f;
        [SerializeField, Range(0f, 1f)] private float shapeExitThreshold = 0.65f;
        [SerializeField, Range(0f, 1f)] private float scoreSmoothing = 0.35f;

        [Header("Recovery")]
        [SerializeField] private float maxRecenterWaitTime = 1.5f;

        [Header("Hand Stability")]
        [Tooltip("Maximum allowed wrist movement while swiping. Prevents whole-hand left/right movement from triggering a swipe. 0.015 = 1.5 cm.")]
        [SerializeField] private float maxAllowedWristMovement = 0.015f;

        [Tooltip("If enabled, any whole-hand movement cancels the armed swipe and returns to waiting for the initial pose.")]
        [SerializeField] private bool cancelSwipeWhenWristMovesTooMuch = true;

        [Header("Thumb Tip Motion Detection")]
        [SerializeField] private Transform swipeReferenceTransform;
        [SerializeField] private XRHandJointID thumbTipJoint = XRHandJointID.ThumbTip;
        [SerializeField] private XRHandJointID thumbAnchorJoint = XRHandJointID.Wrist;

        [Tooltip("How far the thumb must move away from the initial position before the swipe timer starts. 0.012 = 1.2 cm.")]
        [SerializeField] private float thumbInitialBreakDistance = 0.012f;

        [Tooltip("How far the thumb must move left/right from the initial position to count as a swipe. 0.035 = 3.5 cm.")]
        [SerializeField] private float thumbSwipeDistance = 0.035f;

        [Tooltip("How close the thumb must return to the initial position before another swipe can be armed. 0.015 = 1.5 cm.")]
        [SerializeField] private float thumbRecenterDistance = 0.015f;

        [SerializeField] private bool invertThumbSwipeDirection;

        [Header("Timing")]
        [SerializeField] private float swipeTime = 0.35f;
        [SerializeField] private float recenterHoldTime = 0.15f;
        [SerializeField] private float gestureDetectionInterval = 0.02f;

        [Header("XR Turn")]
        [SerializeField] private Transform rotationRoot;
        [SerializeField] private Transform headTransform;
        [SerializeField] private float turnAngle = 45f;
        [SerializeField] private bool rotateOnSwipe = true;

        [Header("Events")]
        [SerializeField] private UnityEvent initialGestureArmed;
        [SerializeField] private UnityEvent swipeRightSucceeded;
        [SerializeField] private UnityEvent swipeLeftSucceeded;
        [SerializeField] private UnityEvent swipeFailed;

        [Header("Debug")]
        [SerializeField] private bool logInitialScore;
        [SerializeField] private bool logThumbDelta;
        [SerializeField] private float debugLogInterval = 0.25f;

        private XRHandShape initialShape;
        private XRHandPose initialPose;

        private GestureState state = GestureState.WaitingForInitial;
        private GestureRuntimeData initialRuntime;

        private XRHandTrackingEvents subscribedHandTrackingEvents;
        private Coroutine findHandTrackingEventsCoroutine;

        private float lastConditionCheckTime;
        private float initialHoldStartTime;
        private float swipeStartTime;
        private float recenterStartTime;
        private float nextDebugLogTime;

        private Vector3 thumbStartLocalPosition;
        private bool hasThumbStartPosition;

        private Vector3 wristStartLocalPosition;
        private bool hasWristStartPosition;
        private float recenterStateStartTime;

        private void Awake()
        {
            if (completenessCalculator == null)
                completenessCalculator = GetComponent<HandShapeCompletenessCalculator>();
        }

        private void OnEnable()
        {
            CacheInitialGesture();
            ResetToWaitingForInitial();

            if (handTrackingEvents != null)
            {
                SubscribeToHandTrackingEvents(handTrackingEvents);
                return;
            }

            if (findHandTrackingEventsAtRuntime)
                findHandTrackingEventsCoroutine = StartCoroutine(FindHandTrackingEventsRoutine());
        }

        private void OnDisable()
        {
            if (findHandTrackingEventsCoroutine != null)
            {
                StopCoroutine(findHandTrackingEventsCoroutine);
                findHandTrackingEventsCoroutine = null;
            }

            UnsubscribeFromHandTrackingEvents();
            ResetToWaitingForInitial();
        }

        private void OnValidate()
        {
            shapeEnterThreshold = Mathf.Clamp01(shapeEnterThreshold);
            shapeExitThreshold = Mathf.Clamp01(shapeExitThreshold);

            if (shapeExitThreshold > shapeEnterThreshold)
                shapeExitThreshold = shapeEnterThreshold;

            scoreSmoothing = Mathf.Clamp01(scoreSmoothing);

            findRetryInterval = Mathf.Max(0.05f, findRetryInterval);
            gestureDetectionInterval = Mathf.Max(0f, gestureDetectionInterval);

            initialHoldTime = Mathf.Max(0f, initialHoldTime);
            swipeTime = Mathf.Max(0.01f, swipeTime);
            recenterHoldTime = Mathf.Max(0f, recenterHoldTime);

            thumbInitialBreakDistance = Mathf.Max(0.001f, thumbInitialBreakDistance);
            thumbSwipeDistance = Mathf.Max(0.001f, thumbSwipeDistance);
            thumbRecenterDistance = Mathf.Max(0.001f, thumbRecenterDistance);

            debugLogInterval = Mathf.Max(0.05f, debugLogInterval);

            maxAllowedWristMovement = Mathf.Max(0.001f, maxAllowedWristMovement);
            maxRecenterWaitTime = Mathf.Max(0.25f, maxRecenterWaitTime);
        }

        private IEnumerator FindHandTrackingEventsRoutine()
        {
            while (isActiveAndEnabled && handTrackingEvents == null)
            {
                XRHandTrackingEvents foundEvents = FindMatchingHandTrackingEvents();

                if (foundEvents != null)
                {
                    SetHandTrackingEvents(foundEvents);
                    findHandTrackingEventsCoroutine = null;
                    yield break;
                }

                yield return new WaitForSeconds(findRetryInterval);
            }

            findHandTrackingEventsCoroutine = null;
        }

        private XRHandTrackingEvents FindMatchingHandTrackingEvents()
        {
            XRHandTrackingEvents[] eventsComponents =
                Object.FindObjectsByType<XRHandTrackingEvents>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);

            foreach (XRHandTrackingEvents eventsComponent in eventsComponents)
            {
                if (eventsComponent == null)
                    continue;

                if (eventsComponent.handedness == handedness)
                    return eventsComponent;
            }

            return null;
        }

        private void SetHandTrackingEvents(XRHandTrackingEvents newHandTrackingEvents)
        {
            if (newHandTrackingEvents == handTrackingEvents)
                return;

            UnsubscribeFromHandTrackingEvents();

            handTrackingEvents = newHandTrackingEvents;

            if (handTrackingEvents != null)
                SubscribeToHandTrackingEvents(handTrackingEvents);
        }

        private void SubscribeToHandTrackingEvents(XRHandTrackingEvents eventsComponent)
        {
            if (eventsComponent == null)
                return;

            if (subscribedHandTrackingEvents == eventsComponent)
                return;

            subscribedHandTrackingEvents = eventsComponent;
            subscribedHandTrackingEvents.jointsUpdated.AddListener(OnJointsUpdated);
            subscribedHandTrackingEvents.trackingLost.AddListener(OnTrackingLost);
        }

        private void UnsubscribeFromHandTrackingEvents()
        {
            if (subscribedHandTrackingEvents == null)
                return;

            subscribedHandTrackingEvents.jointsUpdated.RemoveListener(OnJointsUpdated);
            subscribedHandTrackingEvents.trackingLost.RemoveListener(OnTrackingLost);
            subscribedHandTrackingEvents = null;
        }

        private void CacheInitialGesture()
        {
            initialShape = initialHandShapeOrPose as XRHandShape;
            initialPose = initialHandShapeOrPose as XRHandPose;

            AssignTargetTransform(initialPose, initialTargetTransform);
        }

        private static void AssignTargetTransform(XRHandPose pose, Transform targetTransform)
        {
            if (pose == null)
                return;

            if (pose.relativeOrientation == null)
                return;

            pose.relativeOrientation.targetTransform = targetTransform;
        }

        private void OnTrackingLost()
        {
            ResetToWaitingForInitial();
        }

        private void OnJointsUpdated(XRHandJointsUpdatedEventArgs eventArgs)
        {
            if (!isActiveAndEnabled)
                return;

            if (handTrackingEvents == null || !handTrackingEvents.handIsTracked)
            {
                ResetToWaitingForInitial();
                return;
            }

            float time = Time.timeSinceLevelLoad;

            if (time < lastConditionCheckTime + gestureDetectionInterval)
                return;

            lastConditionCheckTime = time;

            bool initialDetected = CheckInitialGesture(eventArgs);

            DebugLog(time, eventArgs, initialDetected);

            switch (state)
            {
                case GestureState.WaitingForInitial:
                    UpdateWaitingForInitial(initialDetected, time);
                    break;

                case GestureState.HoldingInitial:
                    UpdateHoldingInitial(initialDetected, time, eventArgs);
                    break;

                case GestureState.Armed:
                    UpdateArmed(time, eventArgs);
                    break;

                case GestureState.WaitingForTarget:
                    UpdateWaitingForTarget(time, eventArgs);
                    break;

                case GestureState.WaitingForRecenterAfterSwipe:
                    UpdateWaitingForRecenterAfterSwipe(initialDetected, time, eventArgs);
                    break;
            }
        }

        private bool CheckInitialGesture(XRHandJointsUpdatedEventArgs eventArgs)
        {
            bool shapeDetected = CheckInitialShape(eventArgs);
            bool poseDetected = initialPose != null && initialPose.CheckConditions(eventArgs);

            return shapeDetected || poseDetected;
        }

        private bool CheckInitialShape(XRHandJointsUpdatedEventArgs eventArgs)
        {
            if (initialShape == null)
            {
                initialRuntime.Reset();
                return false;
            }

            if (completenessCalculator == null)
            {
                bool hardDetected = initialShape.CheckConditions(eventArgs);
                UpdateRuntimeDataFromHardResult(hardDetected);
                return hardDetected;
            }

            bool hasScore = completenessCalculator.TryCalculateHandShapeCompletenessScore(
                eventArgs.hand,
                initialShape,
                out float rawScore);

            if (!hasScore)
            {
                initialRuntime.Reset();
                return false;
            }

            UpdateRuntimeDataFromScore(rawScore);
            return initialRuntime.IsDetected;
        }

        private void UpdateRuntimeDataFromScore(float rawScore)
        {
            initialRuntime.RawScore = Mathf.Clamp01(rawScore);

            if (!initialRuntime.HasScore)
            {
                initialRuntime.SmoothedScore = initialRuntime.RawScore;
                initialRuntime.HasScore = true;
            }
            else
            {
                initialRuntime.SmoothedScore = Mathf.Lerp(
                    initialRuntime.SmoothedScore,
                    initialRuntime.RawScore,
                    scoreSmoothing);
            }

            if (!initialRuntime.IsDetected && initialRuntime.SmoothedScore >= shapeEnterThreshold)
            {
                initialRuntime.IsDetected = true;
            }
            else if (initialRuntime.IsDetected && initialRuntime.SmoothedScore <= shapeExitThreshold)
            {
                initialRuntime.IsDetected = false;
            }
        }

        private void UpdateRuntimeDataFromHardResult(bool detected)
        {
            float targetScore = detected ? 1f : 0f;

            initialRuntime.RawScore = targetScore;

            if (!initialRuntime.HasScore)
            {
                initialRuntime.SmoothedScore = targetScore;
                initialRuntime.HasScore = true;
            }
            else
            {
                initialRuntime.SmoothedScore = Mathf.Lerp(
                    initialRuntime.SmoothedScore,
                    targetScore,
                    scoreSmoothing);
            }

            initialRuntime.IsDetected = detected;
        }

        private void UpdateWaitingForInitial(bool initialDetected, float time)
        {
            if (!initialDetected)
                return;

            initialHoldStartTime = time;
            state = GestureState.HoldingInitial;
        }

        private void UpdateHoldingInitial(
            bool initialDetected,
            float time,
            XRHandJointsUpdatedEventArgs eventArgs)
        {
            if (!initialDetected)
            {
                ResetToWaitingForInitial();
                return;
            }

            if (time - initialHoldStartTime < initialHoldTime)
                return;

            if (!TryCacheThumbStartPosition(eventArgs.hand))
            {
                ResetToWaitingForInitial();
                return;
            }

            state = GestureState.Armed;
            initialGestureArmed?.Invoke();
        }

        private void UpdateArmed(float time, XRHandJointsUpdatedEventArgs eventArgs)
        {
            if (!hasThumbStartPosition || !hasWristStartPosition)
            {
                if (!TryCacheThumbStartPosition(eventArgs.hand))
                {
                    ResetToWaitingForInitial();
                    return;
                }
            }

            if (HasWristMovedTooMuch(eventArgs.hand))
            {
                if (cancelSwipeWhenWristMovesTooMuch)
                    ResetToWaitingForInitial();

                return;
            }

            if (!HasThumbBrokenInitialPosition(eventArgs.hand))
                return;

            swipeStartTime = time;
            state = GestureState.WaitingForTarget;
        }

        private void UpdateWaitingForTarget(float time, XRHandJointsUpdatedEventArgs eventArgs)
        {
            float elapsed = time - swipeStartTime;

            if (elapsed > swipeTime)
            {
                swipeFailed?.Invoke();
                ResetToWaitingForInitial();
                return;
            }

            if (HasWristMovedTooMuch(eventArgs.hand))
            {
                if (cancelSwipeWhenWristMovesTooMuch)
                {
                    swipeFailed?.Invoke();
                    ResetToWaitingForInitial();
                }

                return;
            }

            SwipeDirection detectedDirection = GetThumbSwipeDirection(eventArgs.hand);

            switch (detectedDirection)
            {
                case SwipeDirection.Right:
                    PerformSwipeRight();
                    break;

                case SwipeDirection.Left:
                    PerformSwipeLeft();
                    break;
            }
        }

        private void UpdateWaitingForRecenterAfterSwipe(
    bool initialDetected,
    float time,
    XRHandJointsUpdatedEventArgs eventArgs)
        {
            if (time - recenterStateStartTime > maxRecenterWaitTime)
            {
                ResetToWaitingForInitial();
                return;
            }

            if (!initialDetected)
            {
                recenterStartTime = 0f;
                return;
            }

            if (recenterStartTime <= 0f)
                recenterStartTime = time;

            if (time - recenterStartTime < recenterHoldTime)
                return;

            ResetToWaitingForInitial();
        }

        private bool TryCacheThumbStartPosition(XRHand hand)
        {
            if (!TryGetThumbLocalPosition(hand, out thumbStartLocalPosition))
            {
                hasThumbStartPosition = false;
                hasWristStartPosition = false;
                return false;
            }

            if (!TryGetJointLocalPosition(hand, thumbAnchorJoint, out wristStartLocalPosition))
            {
                hasThumbStartPosition = false;
                hasWristStartPosition = false;
                return false;
            }

            hasThumbStartPosition = true;
            hasWristStartPosition = true;

            return true;
        }

        private bool TryGetJointLocalPosition(
    XRHand hand,
    XRHandJointID jointId,
    out Vector3 localPosition)
        {
            localPosition = Vector3.zero;

            XRHandJoint joint = hand.GetJoint(jointId);

            if (!joint.TryGetPose(out Pose jointPose))
                return false;

            Transform referenceTransform = swipeReferenceTransform != null
                ? swipeReferenceTransform
                : headTransform;

            if (referenceTransform != null)
                localPosition = referenceTransform.InverseTransformPoint(jointPose.position);
            else
                localPosition = jointPose.position;

            return true;
        }

        private bool HasWristMovedTooMuch(XRHand hand)
        {
            if (!hasWristStartPosition)
                return true;

            if (!TryGetJointLocalPosition(hand, thumbAnchorJoint, out Vector3 currentWristLocalPosition))
                return true;

            Vector3 wristDelta = currentWristLocalPosition - wristStartLocalPosition;

            float horizontalWristMovement = Mathf.Abs(wristDelta.x);

            return horizontalWristMovement > maxAllowedWristMovement;
        }

        private bool TryGetThumbDelta(XRHand hand, out Vector3 delta)
        {
            delta = Vector3.zero;

            if (!hasThumbStartPosition)
                return false;

            if (!TryGetThumbLocalPosition(hand, out Vector3 currentThumbLocalPosition))
                return false;

            delta = currentThumbLocalPosition - thumbStartLocalPosition;
            return true;
        }

        private bool TryGetThumbLocalPosition(XRHand hand, out Vector3 localPosition)
        {
            localPosition = Vector3.zero;

            XRHandJoint thumbTip = hand.GetJoint(thumbTipJoint);
            XRHandJoint anchor = hand.GetJoint(thumbAnchorJoint);

            if (!thumbTip.TryGetPose(out Pose thumbTipPose))
                return false;

            if (!anchor.TryGetPose(out Pose anchorPose))
                return false;

            Vector3 thumbOffsetFromAnchor = thumbTipPose.position - anchorPose.position;

            Transform referenceTransform = swipeReferenceTransform != null
                ? swipeReferenceTransform
                : headTransform;

            if (referenceTransform != null)
                localPosition = referenceTransform.InverseTransformDirection(thumbOffsetFromAnchor);
            else
                localPosition = thumbOffsetFromAnchor;

            return true;
        }

        private bool HasThumbBrokenInitialPosition(XRHand hand)
        {
            if (!TryGetThumbDelta(hand, out Vector3 delta))
                return false;

            float horizontalDelta = GetSignedHorizontalDelta(delta);

            return Mathf.Abs(horizontalDelta) >= thumbInitialBreakDistance;
        }

        private SwipeDirection GetThumbSwipeDirection(XRHand hand)
        {
            if (!TryGetThumbDelta(hand, out Vector3 delta))
                return SwipeDirection.None;

            float horizontalDelta = GetSignedHorizontalDelta(delta);

            if (horizontalDelta >= thumbSwipeDistance)
                return SwipeDirection.Right;

            if (horizontalDelta <= -thumbSwipeDistance)
                return SwipeDirection.Left;

            return SwipeDirection.None;
        }

        private bool IsThumbRecentered(XRHand hand)
        {
            if (!TryGetThumbDelta(hand, out Vector3 delta))
                return false;

            float horizontalDelta = GetSignedHorizontalDelta(delta);

            return Mathf.Abs(horizontalDelta) <= thumbRecenterDistance;
        }

        private float GetSignedHorizontalDelta(Vector3 localDelta)
        {
            float horizontalDelta = localDelta.x;

            if (invertThumbSwipeDirection)
                horizontalDelta = -horizontalDelta;

            return horizontalDelta;
        }

        private void PerformSwipeRight()
        {
            if (rotateOnSwipe)
                TurnRight();

            swipeRightSucceeded?.Invoke();
            EnterRecenterState();
        }

        private void PerformSwipeLeft()
        {
            if (rotateOnSwipe)
                TurnLeft();

            swipeLeftSucceeded?.Invoke();
            EnterRecenterState();
        }

        private void EnterRecenterState()
        {
            recenterStartTime = 0f;
            recenterStateStartTime = Time.timeSinceLevelLoad;
            state = GestureState.WaitingForRecenterAfterSwipe;
        }

        public void TurnRight()
        {
            RotateRig(turnAngle);
        }

        public void TurnLeft()
        {
            RotateRig(-turnAngle);
        }

        private void RotateRig(float angle)
        {
            if (rotationRoot == null)
                return;

            if (headTransform != null)
                rotationRoot.RotateAround(headTransform.position, Vector3.up, angle);
            else
                rotationRoot.Rotate(0f, angle, 0f, Space.World);
        }

        private void ResetToWaitingForInitial()
        {
            state = GestureState.WaitingForInitial;

            initialHoldStartTime = 0f;
            swipeStartTime = 0f;
            recenterStartTime = 0f;

            hasThumbStartPosition = false;
            thumbStartLocalPosition = Vector3.zero;

            hasWristStartPosition = false;
            wristStartLocalPosition = Vector3.zero;

            initialRuntime.Reset();
        }

        private void DebugLog(
            float time,
            XRHandJointsUpdatedEventArgs eventArgs,
            bool initialDetected)
        {
            if (!logInitialScore && !logThumbDelta)
                return;

            if (time < nextDebugLogTime)
                return;

            nextDebugLogTime = time + debugLogInterval;

            string message = $"Dynamic hand gesture | State: {state}";

            if (logInitialScore)
            {
                message +=
                    $" | Initial Raw: {initialRuntime.RawScore:0.00}" +
                    $" | Initial Smooth: {initialRuntime.SmoothedScore:0.00}" +
                    $" | Initial Detected: {initialDetected}";
            }

            if (logThumbDelta && TryGetThumbDelta(eventArgs.hand, out Vector3 delta))
            {
                float horizontalDelta = GetSignedHorizontalDelta(delta);

                message +=
                    $" | Thumb X Delta: {horizontalDelta:0.000}" +
                    $" | Thumb Local Delta: {delta}";
            }

            Debug.Log(message, this);
        }
    }
}