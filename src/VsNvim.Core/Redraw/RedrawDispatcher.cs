using System;

namespace VsNvim.Core.Redraw
{
    /// <summary>Decodes a "redraw" notification payload into typed events. Unknown events are ignored.</summary>
    public sealed class RedrawDispatcher
    {
        public event Action<ModeChangeEvent> ModeChanged;
        public event Action<CursorGotoEvent> CursorGoto;
        public event Action FlushReceived;

        public void Process(object[] redrawArgs)
        {
            if (redrawArgs == null)
                return;

            foreach (object batchObj in redrawArgs)
            {
                var batch = (object[])batchObj;
                var name = (string)batch[0];
                switch (name)
                {
                    case "flush":
                        FlushReceived?.Invoke();
                        break;
                    case "mode_change":
                        for (int i = 1; i < batch.Length; i++)
                        {
                            var t = (object[])batch[i];
                            ModeChanged?.Invoke(new ModeChangeEvent((string)t[0], Convert.ToInt64(t[1])));
                        }
                        break;
                    case "grid_cursor_goto":
                        for (int i = 1; i < batch.Length; i++)
                        {
                            var t = (object[])batch[i];
                            CursorGoto?.Invoke(new CursorGotoEvent(
                                Convert.ToInt64(t[0]), Convert.ToInt64(t[1]), Convert.ToInt64(t[2])));
                        }
                        break;
                    // All other redraw events are not needed for the MVP.
                }
            }
        }
    }
}
