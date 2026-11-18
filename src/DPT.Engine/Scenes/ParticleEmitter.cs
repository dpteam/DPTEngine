//
// DPT.Engine - particle emitters.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

using DPT.Engine.Mathematics;

namespace DPT.Engine.Scenes
{
    /// <summary>The shape a new particle starts in.</summary>
    public enum EmitterShape
    {
        /// <summary>All particles start at the emitter's origin.</summary>
        Point = 0,

        /// <summary>Uniformly inside a sphere of the emitter's radius.</summary>
        Sphere = 1,

        /// <summary>Uniformly inside an axis-aligned box of the emitter's size.</summary>
        Box = 2,

        /// <summary>On a disc in the emitter's local XY plane.</summary>
        Disc = 3
    }

    /// <summary>One live particle.</summary>
    public struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Lifetime;
        public float Size;
        public float Rotation;
        public float RotationSpeed;
        public bool Alive;
    }

    /// <summary>
    /// A particle emitter attached to a scene node.
    ///
    /// Simulation is in world space and stepped by frame count rather than by
    /// wall-clock time, so the same frame number always produces the same
    /// particles. That is what lets the editor scrub the timeline and see exactly
    /// what the runtime will show, and what keeps two machines in sync in a
    /// networked session.
    ///
    /// The random generator is a seeded xorshift owned by the emitter, not
    /// <see cref="System.Random"/>: the framework's implementation is allowed to
    /// change between runtime versions, and a demo whose sparks scatter
    /// differently on a patched machine is a bug nobody can reproduce.
    /// </summary>
    public sealed class ParticleEmitter
    {
        public EmitterShape Shape = EmitterShape.Point;

        public int MaxParticles = 512;

        /// <summary>Particles spawned per simulation step.</summary>
        public float Rate = 8f;

        /// <summary>Fractional spawns are carried between steps, so a rate below 1 still works.</summary>
        private float spawnAccumulator;

        public float Lifetime = 1.5f;
        public float LifetimeChaos = 0.5f;

        public float Speed = 3f;
        public float SpeedChaos = 1f;

        /// <summary>Emission direction in emitter space. Zero means "outward from the shape".</summary>
        public Vector3 Direction = new Vector3(0f, 1f, 0f);

        /// <summary>Cone half-angle in degrees. 180 scatters in every direction.</summary>
        public float Spread = 25f;

        public float StartSize = 0.5f;
        public float EndSize = 0.05f;
        public float SizeChaos = 0.3f;

        public ColorRgba StartColour = new ColorRgba(255, 200, 80, 255);
        public ColorRgba EndColour = new ColorRgba(255, 40, 0, 0);

        public Vector3 Gravity = new Vector3(0f, -4f, 0f);

        /// <summary>Radius for <see cref="EmitterShape.Sphere"/> and <see cref="EmitterShape.Disc"/>.</summary>
        public float Radius = 0.5f;

        /// <summary>Half-extents for <see cref="EmitterShape.Box"/>.</summary>
        public Vector3 BoxSize = new Vector3(0.5f, 0.5f, 0.5f);

        /// <summary>Rotation applied to spawned particles, in degrees per second.</summary>
        public float Spin = 45f;

        /// <summary>Seed for the emitter's generator. Same seed, same particles.</summary>
        public int Seed = 12345;

        private Particle[] particles = new Particle[0];
        private uint randomState;

        public Particle[] Particles
        {
            get { return particles; }
        }

        public int MaxParticlesConfigured
        {
            get { return MaxParticles; }
        }

        public ParticleEmitter()
        {
            Reset();
        }

        /// <summary>Kills every particle and rewinds the generator.</summary>
        public void Reset()
        {
            var capacity = MaxParticles < 1 ? 1 : MaxParticles;

            if (particles.Length != capacity)
            {
                particles = new Particle[capacity];
            }

            for (var i = 0; i < particles.Length; i++)
            {
                particles[i].Alive = false;
            }

            spawnAccumulator = 0f;
            randomState = (uint)(Seed == 0 ? 1 : Seed) | 1u;
        }

        /// <summary>Number of live particles, for the editor's statistics.</summary>
        public int LiveCount
        {
            get
            {
                var count = 0;

                for (var i = 0; i < particles.Length; i++)
                {
                    if (particles[i].Alive) count++;
                }

                return count;
            }
        }

        /// <summary>
        /// Advances the simulation.
        ///
        /// <paramref name="world"/> is the owning node's transform: emitters move
        /// with their node, but particles already in flight stay where they are.
        /// </summary>
        public void Update(Matrix world, int steps)
        {
            if (steps < 1) steps = 1;
            if (steps > 100) steps = 100; // a long scrub must not stall the frame

            // The simulation step is one 100 Hz frame, matching the timeline.
            const float delta = 0.01f;

            for (var step = 0; step < steps; step++)
            {
                Spawn(world, delta);
                Integrate(delta);
            }
        }

        private void Spawn(Matrix world, float delta)
        {
            spawnAccumulator += Rate * (delta / 0.01f);

            while (spawnAccumulator >= 1f)
            {
                spawnAccumulator -= 1f;

                var index = FindFreeSlot();

                if (index < 0) return;

                var particle = new Particle();

                particle.Position = Vector3.Transform(SpawnPosition(), world);
                particle.Velocity = SpawnVelocity(world);

                particle.Lifetime = Math.Max(0.05f, Lifetime + NextFloat() * LifetimeChaos);
                particle.Age = 0f;
                particle.Size = Math.Max(0.001f, StartSize * (1f + NextFloat() * SizeChaos));
                particle.Rotation = NextFloat() * 360f;
                particle.RotationSpeed = NextFloat() * Spin;
                particle.Alive = true;

                particles[index] = particle;
            }
        }

        private int FindFreeSlot()
        {
            for (var i = 0; i < particles.Length; i++)
            {
                if (!particles[i].Alive) return i;
            }

            // Recycle the oldest rather than dropping the spawn: a full emitter
            // should keep looking alive, not go quiet.
            var oldest = 0;
            var best = -1f;

            for (var i = 0; i < particles.Length; i++)
            {
                var progress = particles[i].Lifetime > 0
                    ? particles[i].Age / particles[i].Lifetime
                    : 1f;

                if (progress > best)
                {
                    best = progress;
                    oldest = i;
                }
            }

            return oldest;
        }

        private Vector3 SpawnPosition()
        {
            switch (Shape)
            {
                case EmitterShape.Sphere:
                {
                    var direction = RandomDirection();
                    return direction * (NextFloat() * Radius);
                }

                case EmitterShape.Box:
                    return new Vector3(
                        NextFloat() * BoxSize.X,
                        NextFloat() * BoxSize.Y,
                        NextFloat() * BoxSize.Z);

                case EmitterShape.Disc:
                {
                    var angle = NextFloat() * MathHelper.TwoPi;
                    var radius = NextFloat() * Radius;
                    return new Vector3((float)Math.Cos(angle) * radius,
                        (float)Math.Sin(angle) * radius, 0f);
                }

                default:
                    return Vector3.Zero;
            }
        }

        private Vector3 SpawnVelocity(Matrix world)
        {
            var direction = Direction;

            if (direction.LengthSquared < 1e-8f)
            {
                // "Outward" only has a meaning for a shape; a point emitter with
                // no direction would otherwise spawn stationary particles.
                direction = Shape == EmitterShape.Point ? Vector3.Up : RandomDirection();
            }
            else
            {
                direction.Normalize();
            }

            // Cone spread around the direction.
            var spread = MathHelper.ToRadians(MathHelper.Clamp(Spread, 0f, 180f));

            if (spread > 0f)
            {
                var axis = Vector3.Cross(direction, RandomDirection());

                if (axis.LengthSquared < 1e-8f)
                {
                    axis = Vector3.Cross(direction, Vector3.Right);
                }

                if (axis.LengthSquared > 1e-8f)
                {
                    axis.Normalize();
                    var angle = (NextFloat() * 0.5f + 0.5f) * spread;
                    var rotation = Matrix.CreateFromAxisAngle(axis, angle);
                    direction = Vector3.TransformNormal(direction, rotation);
                }
            }

            // Direction is in emitter space; velocity must be in world space.
            var worldDirection = Vector3.TransformNormal(direction, world);

            if (worldDirection.LengthSquared < 1e-8f) worldDirection = Vector3.Up;

            worldDirection.Normalize();

            var speed = Math.Max(0f, Speed + NextFloat() * SpeedChaos);
            return worldDirection * speed;
        }

        private void Integrate(float delta)
        {
            for (var i = 0; i < particles.Length; i++)
            {
                if (!particles[i].Alive) continue;

                var particle = particles[i];

                particle.Age += delta;

                if (particle.Age >= particle.Lifetime)
                {
                    particle.Alive = false;
                    particles[i] = particle;
                    continue;
                }

                particle.Velocity += Gravity * delta;
                particle.Position += particle.Velocity * delta;
                particle.Rotation += particle.RotationSpeed * delta;

                particles[i] = particle;
            }
        }

        /// <summary>Colour and size for a particle, at its current age.</summary>
        public void Sample(ref Particle particle, out ColorRgba colour, out float size)
        {
            var progress = particle.Lifetime > 0f ? particle.Age / particle.Lifetime : 1f;

            if (progress < 0f) progress = 0f;
            if (progress > 1f) progress = 1f;

            colour = ColorRgba.Lerp(StartColour, EndColour, progress);
            size = particle.Size + (EndSize - StartSize) * progress;
        }

        private Vector3 RandomDirection()
        {
            // Rejection sampling in the unit cube gives a uniform direction
            // without a trigonometry-heavy spherical distribution.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = new Vector3(NextFloat(), NextFloat(), NextFloat());

                var lengthSquared = candidate.LengthSquared;

                if (lengthSquared > 1e-4f && lengthSquared <= 1f)
                {
                    candidate.Normalize();
                    return candidate;
                }
            }

            return Vector3.Up;
        }

        /// <summary>xorshift32, returning -1..1.</summary>
        private float NextFloat()
        {
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;

            return (randomState & 0xFFFF) / 32767.5f - 1f;
        }
    }
}
