using System;
using UnityEngine.Rendering;
using UnityEngine.Experimental.XR;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public struct MultipassCamera
    {
        public Camera m_Camera;
        public int m_PassIndex;
        static private MultipassCamera[] s_Cameras = null;

        public MultipassCamera(Camera camera = null, int passIndex = 0)
        {
            m_Camera = camera;
            m_PassIndex = passIndex;
        }

        public Camera camera { get { return m_Camera; } }
        public int passIndex { get { return m_PassIndex; } }

        static public MultipassCamera[] SetupFrame(Camera[] cameras, XRDisplaySubsystem xrDisplay)
        {
            int passCount = 1;

            // XR SDK
            if (xrDisplay != null)
            {
                passCount = xrDisplay.GetRenderPassCount();
                xrDisplay.disableLegacyRenderer = true;
            }
            else
            {
                // XR legacy multi-pass rendering using C++ engine
                if (XRGraphics.enabled && XRGraphics.stereoRenderingMode == XRGraphics.StereoRenderingMode.MultiPass)
                    passCount = 2;
            }

            // Late and cached allocation
            int cameraCount = cameras.Length * passCount;
            if (s_Cameras == null || s_Cameras.Length != cameraCount)
                s_Cameras = new MultipassCamera[cameraCount];

            int cameraIndex = 0;
            foreach (var camera in cameras)
            {
                if (xrDisplay != null)
                {
                    for (int passIndex = 1; passIndex <= passCount; ++passIndex)
                    {
                        XRDisplaySubsystem.XRRenderPass xrRenderPass = new XRDisplaySubsystem.XRRenderPass();
                        if (xrDisplay.TryGetRenderPass(passIndex - 1, out xrRenderPass))
                        {
                            s_Cameras[cameraIndex++] = new MultipassCamera(camera, passIndex);
                        }
                    }
                }
                else if (camera.stereoEnabled && passCount > 1)
                {
                    // pass 0 is used only when multi-pass is not active
                    for (int passIndex = 1; passIndex <= passCount; ++passIndex)
                    {
                        s_Cameras[cameraIndex++] = new MultipassCamera(camera, passIndex);
                    }
                }
                else
                {
                    s_Cameras[cameraIndex++] = new MultipassCamera(camera, 0);
                }
            }

            return s_Cameras;
        }
    }
}
