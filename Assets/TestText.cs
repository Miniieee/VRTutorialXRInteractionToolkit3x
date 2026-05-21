using TMPro;
using UnityEngine;

public class TestText : MonoBehaviour
{
    public TMP_Text text;

    public void StartedRightInitialGesture()
    {
        text.text = "Started Right Initial Gesture";
    }

    public void StartedLeftInitialGesture()
    {
        text.text = "Started Left Initial Gesture Aiming for Target Gesture";
    }

    public void PerformedLeftTargetGesture()
    {
        text.text = "Ended Left Initial Gesture: Teleported to location";
    }

    public void PerformedSwipeRightTargetGesture()
    {
        text.text = "Performed Right Target Gesture: Swipe Right";
    }

    public void PerformedSwipeLeftTargetGesture()
    {
        text.text = "Performed Left Target Gesture: Swipe Left";
    }

    public void Failed()
    {
        text.text = "Failed to perform gesture";
    }
}
