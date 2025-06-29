using UnityEngine;
using UnityEngine.InputSystem;

public class HandAnimation : MonoBehaviour
{
    [SerializeField] private InputActionReference gripActionRefrerence;
    [SerializeField] private InputActionReference triggerActionRefrerence;

    private Animator animator;

    private string gripActionName = "Grip";
    private string triggerActionName = "Trigger";

    private void Awake()
    {
        animator = GetComponent<Animator>();
        if (animator == null)
        {
            Debug.LogError("Animator component not found on the GameObject.");
        }
    }

    void Update()
    {
        if (animator == null)
        {
            return; // Exit if animator is not set
        }

        float gripValue = gripActionRefrerence.action.ReadValue<float>();
        float triggerValue = triggerActionRefrerence.action.ReadValue<float>();

        animator.SetFloat(gripActionName, gripValue);
        animator.SetFloat(triggerActionName, triggerValue);
    
    }
}
