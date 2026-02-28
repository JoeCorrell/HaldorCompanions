using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Guarantees companion bow accuracy by redirecting projectile velocity
    /// toward the target's center mass with gravity compensation and velocity lead.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
    internal static class ProjectileSetup_CompanionAccuracy
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> _velRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("m_vel");

        private static float _lastLogTime;

        static void Postfix(Projectile __instance, Character owner)
        {
            if (owner == null) return;
            if (owner.GetComponent<CompanionSetup>() == null) return;

            var ai = owner.GetComponent<CompanionAI>();
            if (ai == null) return;

            Character target = ai.m_targetCreature;
            if (target == null || target.IsDead()) return;

            // Redirect toward target center mass — enforce minimum arrow speed
            // (vanilla fires with 0% draw → speed=2 when ChargeStart doesn't work)
            Vector3 currentVel = _velRef(__instance);
            float speed = currentVel.magnitude;
            if (speed < 0.1f) return;
            const float MinArrowSpeed = 60f;
            if (speed < MinArrowSpeed) speed = MinArrowSpeed;

            Vector3 origin = __instance.transform.position;
            Vector3 targetPoint = target.GetCenterPoint();
            float dist = Vector3.Distance(origin, targetPoint);

            // Lead the target based on their velocity
            Vector3 targetVel = target.GetVelocity();
            float travelTime = dist / speed;

            if (targetVel.magnitude > 0.5f)
                targetPoint += targetVel * travelTime;

            // Gravity compensation — arrows drop over distance
            // v = v0 + g*t, so we need to aim higher by 0.5*g*t^2
            float gravityDrop = 0.5f * 9.81f * travelTime * travelTime;
            targetPoint.y += gravityDrop;

            Vector3 toTarget = (targetPoint - origin).normalized;
            _velRef(__instance) = toTarget * speed;

            if (Time.time - _lastLogTime > 1f)
            {
                _lastLogTime = Time.time;
                CompanionsPlugin.Log.LogDebug(
                    $"[Projectile] Arrow redirected — target=\"{target.m_name}\" " +
                    $"dist={dist:F1} speed={speed:F0} travelTime={travelTime:F2}s " +
                    $"gravDrop={gravityDrop:F2} leadMag={targetVel.magnitude * travelTime:F1} " +
                    $"targetVel={targetVel.magnitude:F1}");
            }
        }
    }
}
