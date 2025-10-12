using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class Gun : MonoBehaviour
{
    [SerializeField] Transform spawnPoint;
    [SerializeField] GameObject projectilePrefab;
    private XRGrabInteractableFixed grabInteractableFixed;

    private void Awake()
    {
        grabInteractableFixed = GetComponent<XRGrabInteractableFixed>();
    }

    void Start()
    {
        grabInteractableFixed.activated.AddListener(Shoot);
    }

    private void Shoot(ActivateEventArgs arg0)
    {
        Instantiate(projectilePrefab, spawnPoint.position, spawnPoint.rotation);
    }

    private void OnDestroy()
    {
        grabInteractableFixed.activated.RemoveListener(Shoot);
    }
}
