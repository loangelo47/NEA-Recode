using UnityEngine;
using KinematicCharacterController;

public enum CrouchInput
{
    None, Toggle
}

public enum Stance
{
    Stand, Crouch, Slide
}

public struct CharacterState
{
    public bool Grounded;
    public Stance Stance;
    public Vector3 Velocity;
}

public struct CharacterInput
{
    public Quaternion Rotation;
    public Vector2 Move;
    public bool Jump;
    public bool JumpSustain;
    public CrouchInput Crouch;
}

public class PlayerCharacter : MonoBehaviour, ICharacterController
{
    [SerializeField] private KinematicCharacterMotor motor;
    [SerializeField] private Transform root;
    [SerializeField] private Transform cameraTarget;
    [Space]
    [SerializeField] private float walkSpeed = 20f;
    [SerializeField] private float crouchSpeed = 7f;
    [SerializeField] private float walkResponse = 25f;
    [SerializeField] private float crouchResponse = 20f;
    [Space]
    [SerializeField] private float airSpeed = 15f;
    [SerializeField] private float airAcceleration = 70f;
    [Space]
    [SerializeField] private float jumpSpeed = 20f;
    [SerializeField] private float coyoteTime = 0.2f;
    [Range(0, 1f)]
    [SerializeField] private float jumpSustainGravity = 0.4f;
    [SerializeField] private float gravity = -90f;
    [Space]
    [SerializeField] private float slideStartSpeed = 25f;
    [SerializeField] private float slideEndSpeed = 15f;
    [SerializeField] private float slideFriction = 0.8f;
    [SerializeField] private float slideSteerAcceleration = 5f;
    [SerializeField] private float slideGravity = -90f;
    [Space]
    [SerializeField] private float standHeight = 2f;
    [SerializeField] private float crouchHeight = 1f;
    [SerializeField] private float crouchHeightResponse = 15f;
    [Range(0f, 1f)]
    [SerializeField] private float standCameraTargetHeight = 0.9f;
    [Range(0f, 1f)]
    [SerializeField] private float crouchCameraTargetHeight = 0.7f;

    private Stance _stance;

    private CharacterState _state;
    private CharacterState _lastState;
    private CharacterState _tempState;

    private Quaternion _requestedRotation;
    private Vector3 _requestedMovement;
    private bool _requestedJump;
    private bool _requestedSustainedJump;
    private bool _requestedCrouch;
    private bool _requestedCrouchInAir;

    private float _timeSinceUnground;
    private float _timeSinceJumpRequest;
    private bool _ungroundedDueToJump;

    private Collider[] _uncrouchOverlapResults;

    public void Initialize()
    {
        _state.Stance = Stance.Stand;
        _lastState = _state;

        _uncrouchOverlapResults = new Collider[8];

        motor.CharacterController = this;
    }

    public void UpdateInput(CharacterInput input)
    {
        _requestedRotation = input.Rotation;
        // Take the 2d input vector and create a 3D movement vector on the XZ plane.
        _requestedMovement = new Vector3(input.Move.x, 0f, input.Move.y);
        // Clamp the length to 1 to prevent moving faster diagonally with WASD movement.
        _requestedMovement = Vector3.ClampMagnitude(_requestedMovement, 1f);
        // Orient the input so it's relative to the direction the player is facing
        _requestedMovement = input.Rotation * _requestedMovement;

        var wasRequestJump = _requestedJump;
        _requestedJump = _requestedJump || input.Jump;
        if (_requestedJump && !wasRequestJump)
            _timeSinceJumpRequest = 0f;
        _requestedSustainedJump = input.JumpSustain;

        var wasRequestingCrouch = _requestedCrouch;
        _requestedCrouch = input.Crouch switch
        {
          CrouchInput.Toggle => !_requestedCrouch,
          _ => _requestedCrouch
        };
        if ( _requestedCrouch && !wasRequestingCrouch)
            _requestedCrouchInAir = !_state.Grounded;
        else if (!_requestedCrouch && wasRequestingCrouch)
            _requestedCrouchInAir = false;
    }

    public void UpdateBody(float deltaTime)
    {
        var currentHeight = motor.Capsule.height;
        var normalizedHeight = currentHeight / standHeight;

        var cameraTargetHeight = currentHeight *
        (
            _state.Stance is Stance.Stand
                ? standCameraTargetHeight
                : crouchCameraTargetHeight
        );
        var rootTargetScale = new Vector3(1f, normalizedHeight, 1f);

        cameraTarget.localPosition = Vector3.Lerp
        (
            a: cameraTarget.localPosition,
            b: new Vector3(0f, cameraTargetHeight, 0f),
            t: 1f- Mathf.Exp(-crouchHeightResponse * deltaTime)
        );
        root.localScale = Vector3.Lerp
        (
            a: root.localScale, 
            b: rootTargetScale,
            t: 1f - Mathf.Exp(-crouchHeightResponse * deltaTime)
        );
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (motor.GroundingStatus.IsStableOnGround)
        {
            _timeSinceUnground= 0f;
            _ungroundedDueToJump = false;
            // If on the ground...
            // Snap the requested movement direction to the angle of the surface
            // the character is currently walking on.
            
            var groundedMovement = motor.GetDirectionTangentToSurface
            (
                direction: _requestedMovement,
                surfaceNormal: motor.GroundingStatus.GroundNormal
            ) * _requestedMovement.magnitude;

            // Start Sliding
            {
                var moving = groundedMovement.sqrMagnitude > 0f;
                var crouching = _state.Stance is Stance.Crouch;
                var wasStanding = _lastState.Stance is Stance.Stand;
                var wasInAir = !_lastState.Grounded;
                if (moving && crouching && (wasStanding || wasInAir))
                {
                    Debug.DrawRay(transform.position, currentVelocity, Color.red, 5f);
                    Debug.DrawRay(transform.position, _lastState.Velocity, Color.green, 5f);

                    _state.Stance = Stance.Slide;

                    // When landing on stable the character motor projects the velocity onto a flat ground plane
                    // See: KinematicCharacterMotor.HandleVelocityProjection()
                    // Normally this would be good but sliding in an intentional bit of the game
                    // I want the player to carry some momentum when falling to make the physics more intuitive for the player
                    // Reproject the last frames (falling) velocity onto the ground normal to slide.
                    if (wasInAir)
                    {
                        currentVelocity = Vector3.ProjectOnPlane
                        (
                            vector : _lastState.Velocity,
                            planeNormal: motor.GroundingStatus.GroundNormal
                        );
                    }

                    var effectiveSlideStartSpeed = slideStartSpeed;
                    if (!_lastState.Grounded && !_requestedCrouchInAir)
                    {
                        effectiveSlideStartSpeed = 0f;
                        _requestedCrouchInAir = false;
                    }
                    var slideSpeed = Mathf.Max(slideStartSpeed, currentVelocity.magnitude);
                    currentVelocity = motor.GetDirectionTangentToSurface
                    (
                        direction : currentVelocity,
                        surfaceNormal : motor.GroundingStatus.GroundNormal
                    ) * slideSpeed;
                }
            }
            // Move
            if (_state.Stance is Stance.Stand or Stance.Crouch)
            {
                

                // Calculate the speed and responsiveness of movement based
                // on the character's stance
                var speed = _state.Stance is Stance.Stand
                    ? walkSpeed
                    : crouchSpeed;
                var response = _state.Stance is Stance.Stand
                    ? walkResponse
                    : crouchResponse;
                    
                // and move along the ground direction.
                var targetVelocity = groundedMovement * speed;
                currentVelocity = Vector3.Lerp
                (
                    a: currentVelocity,
                    b: targetVelocity,
                    t: 1f - Mathf.Exp(-response * deltaTime)
                );
            }
            else
            {
                // Friction.
                currentVelocity -= currentVelocity * (slideFriction * deltaTime);

                //slope 
                {
                    var force = Vector3.ProjectOnPlane
                    (
                        vector: -motor.CharacterUp,
                        planeNormal: motor.GroundingStatus.GroundNormal
                    ) * slideGravity;

                    currentVelocity -= force * deltaTime;
                }

                // Steer
                {
                    // Target velocity is the player's movement direction, at the current speed.
                    var currentSpeed = currentVelocity.magnitude;
                    var targetVelocity = groundedMovement * currentVelocity.magnitude;
                    var steerForce = (targetVelocity - currentVelocity) * slideSteerAcceleration * deltaTime;
                    // Add steer force, but clamp speed so the slide doesn't accelerate due to the direct movement input
                    currentVelocity += steerForce;
                    currentVelocity = Vector3.ClampMagnitude(currentVelocity, currentSpeed);
                }

                // Stop.
                if (currentVelocity.magnitude < slideEndSpeed)
                    _state.Stance = Stance.Crouch;
            }
        }
        // else in the air...
        else
        {
            _timeSinceUnground += deltaTime;

            // Move.
            if (_requestedMovement.sqrMagnitude > 0f)
            {
                // Requested movement projected onto movement plane. (magnitude preserved)
                var planarMovement = Vector3.ProjectOnPlane
                (
                    vector: _requestedMovement,
                    planeNormal: motor.CharacterUp
                ) * _requestedMovement.magnitude;

                // current velocity on movement plane
                var currentPlanarVelocity = Vector3.ProjectOnPlane
                (
                    vector: currentVelocity,
                    planeNormal: motor.CharacterUp
                );

                // Calculate movement force
                // Will be changed depending on velocity
                var movementForce = planarMovement * airAcceleration * deltaTime;

                //If moving slower than the max air speed, treat movementForce as a simple steering force.
                if (currentPlanarVelocity.magnitude < airSpeed)
                {
                // Add it to the planar velocity for a target velocity.
                var targetPlanarVelocity = currentPlanarVelocity + movementForce;

                // Limit target velocity to air speed.
                targetPlanarVelocity = Vector3.ClampMagnitude(targetPlanarVelocity, airSpeed);

                movementForce = targetPlanarVelocity - currentPlanarVelocity;
                }
                // Otherwise, nerf the movement force when it is in the direction of the current planar velocity
                // to prevent accelerating further beyond the max air speed
                else if (Vector3.Dot(currentPlanarVelocity, movementForce) > 0f)
                {
                    // Project movement force onto the plane whose normal is the current planar velocity
                    var constrainedMovementForce = Vector3.ProjectOnPlane
                    (
                        vector: movementForce,
                        planeNormal: currentPlanarVelocity.normalized
                    );

                    movementForce = constrainedMovementForce;
                }


                // Steer towards currrent velocity.
                currentVelocity += movementForce;
            }
            // Gravity.
            var effectiveGravity = gravity;
            var verticalSpeed = Vector3.Dot(currentVelocity, motor.CharacterUp);
            if (_requestedSustainedJump && verticalSpeed > 0f)
                effectiveGravity *= jumpSustainGravity;

            currentVelocity += motor.CharacterUp * effectiveGravity * deltaTime;
        }

        if (_requestedJump)
        {
            var grounded = motor.GroundingStatus.IsStableOnGround;
            var canCoyoteJump = _timeSinceUnground < coyoteTime && !_ungroundedDueToJump;

            if (grounded || canCoyoteJump)
            {
            _requestedJump = false;     // Unset jump request.
            _requestedCrouch = false;   // And request the character uncrouched.
            _requestedCrouchInAir = false;

            // unstick the player from the ground.
            motor.ForceUnground(time: 0.1f);
            _ungroundedDueToJump = true;

            // Set minimum vertical speed to the jump speed.
            var currentVerticalSpeed = Vector3.Dot(currentVelocity, motor.CharacterUp);
            var targetVerticalSpeed = Mathf.Max(currentVerticalSpeed, jumpSpeed);
            // Add the difference in current and target vertical speed to the character's velocity.
            currentVelocity += motor.CharacterUp * (targetVerticalSpeed - currentVerticalSpeed);
            }
            else
            {
                _timeSinceJumpRequest += deltaTime;

                // Defer the jump request until coyote time has passed.
                var canJumpLater = _timeSinceJumpRequest < coyoteTime;
                _requestedJump = canJumpLater;
            }
        }
    }
    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        // update the character's rotation to face in the same direction as the
        // resquested rotation (camera rotation)

        // I don't want the character to rotate up and down, so the direction of the character
        // looks should only be locked to X axis / 'flatttened' in a sense

        //this is done by projecting a vector pointing in the same direction that
        //the player is looking onto a flat ground plane

        var forward = Vector3.ProjectOnPlane
        (
            _requestedRotation * Vector3.forward,
            motor.CharacterUp
        );

        if (forward.sqrMagnitude > 0f)
        {
            currentRotation = Quaternion.LookRotation(forward, motor.CharacterUp);
        }
    }

    public void BeforeCharacterUpdate(float deltaTime)
    {
        _tempState = _state;

        // Crouch.
        if (_requestedCrouch && _state.Stance == Stance.Stand)
        {
            _state.Stance = Stance.Crouch;
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: crouchHeight,
                yOffset: crouchHeight * 0.5f
            );
        }
    }
    public void PostGroundingUpdate(float deltatime)
    {
        if (!motor.GroundingStatus.IsStableOnGround && _state.Stance is Stance.Slide)
            _state.Stance = Stance.Crouch;
    }
    public void AfterCharacterUpdate(float deltaTime)
    {
        // Uncrouch.
        if (!_requestedCrouch && _state.Stance is not Stance.Stand)
        {
            // hesitantly 'standup' the character by checking if there's enough space above the character to fit the stand height.
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: standHeight,
                yOffset: standHeight * 0.5f
            );

            // check if capsule overlaps with any colliders before uncrouching.
            var pos = motor.TransientPosition;
            var rot = motor.TransientRotation;
            var mask = motor.CollidableLayers;
            if (motor.CharacterOverlap(pos, rot, _uncrouchOverlapResults, mask, QueryTriggerInteraction.Ignore) > 0)
            {
                // Re-crouch
                _requestedCrouch = true;
                motor.SetCapsuleDimensions
                (
                    radius : motor.Capsule.radius,
                    height : standHeight,
                    yOffset: standHeight * 0.5f
                );
            }
            else
            {
                _state.Stance = Stance.Stand;
            }
        }

        // Update state to reflect relevant motor properties.
        _state.Grounded = motor.GroundingStatus.IsStableOnGround;
        _state.Velocity = motor.Velocity;
        // And update the _lastState to store the character state snapshot taken at
        // the beggining of this character update
        _lastState = _tempState;
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport){}
    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport){}
    public bool IsColliderValidForCollisions(Collider coll) => true;
    public void OnDiscreteCollisionDetected(Collider hitCollider){}
    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 aCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport){} 

    public Transform GetCameraTarget() => cameraTarget;
}
