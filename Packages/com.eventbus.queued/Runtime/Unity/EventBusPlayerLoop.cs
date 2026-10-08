using System;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace QueuedEventBus
{
    internal static class EventBusPlayerLoop
    {
        // This unique type lets us replace/remove only our callback, including across
        // Play sessions with domain reload disabled, without restoring Unity defaults.
        internal struct Dispatch
        {
        }

        internal static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveFrom(ref loop);

            if (!InsertAfterUpdate(ref loop))
            {
                throw new InvalidOperationException(
                    "QueuedEventBus requires Update.ScriptRunBehaviourUpdate in the current PlayerLoop. " +
                    "Install the event bus after configuring a custom PlayerLoop that retains this system.");
            }

            PlayerLoop.SetPlayerLoop(loop);
        }

        internal static void Remove()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();

            if (RemoveFrom(ref loop))
            {
                PlayerLoop.SetPlayerLoop(loop);
            }
        }

        private static bool InsertAfterUpdate(ref PlayerLoopSystem parent)
        {
            var children = parent.subSystemList;

            if (children == null)
            {
                return false;
            }

            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].type == typeof(Update.ScriptRunBehaviourUpdate))
                {
                    parent.subSystemList = InsertDispatchAfter(children, i);
                    return true;
                }

                var child = children[i];

                if (InsertAfterUpdate(ref child))
                {
                    // PlayerLoopSystem is a value type; write the modified child back.
                    children[i] = child;
                    return true;
                }
            }

            return false;
        }

        private static PlayerLoopSystem[] InsertDispatchAfter(PlayerLoopSystem[] children, int index)
        {
            var expanded = new PlayerLoopSystem[children.Length + 1];
            Array.Copy(children, 0, expanded, 0, index + 1);
            expanded[index + 1] = new PlayerLoopSystem
            {
                type = typeof(Dispatch),
                updateDelegate = Events.Dispatch
            };

            Array.Copy(children, index + 1, expanded, index + 2, children.Length - index - 1);
            return expanded;
        }

        private static bool RemoveFrom(ref PlayerLoopSystem parent)
        {
            var children = parent.subSystemList;

            if (children == null)
            {
                return false;
            }

            var remaining = children.Length;
            var changed = false;

            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].type == typeof(Dispatch))
                {
                    remaining--;
                    changed = true;
                    continue;
                }

                var child = children[i];

                if (RemoveFrom(ref child))
                {
                    children[i] = child;
                    changed = true;
                }
            }

            if (remaining != children.Length)
            {
                // Allocate only at levels containing our callback; preserve unrelated systems.
                parent.subSystemList = RetainOtherSystems(children, remaining);
            }

            return changed;
        }

        private static PlayerLoopSystem[] RetainOtherSystems(PlayerLoopSystem[] children, int count)
        {
            var retained = new PlayerLoopSystem[count];
            var next = 0;

            for (var i = 0; i < children.Length; i++)
            {
                if (children[i].type != typeof(Dispatch))
                {
                    retained[next++] = children[i];
                }
            }

            return retained;
        }
    }
}
