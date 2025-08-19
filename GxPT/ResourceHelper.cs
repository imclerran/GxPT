using System;
using System.Drawing;
using System.Reflection;

namespace GxPT
{
    class ResourceHelper
    {
        public static Image GetAssemblyImage(string imageName)
        {
            // Define your namespace here
            string namespacePrefix = "GxPT";
            Assembly assembly = Assembly.GetExecutingAssembly();

            // Construct the full resource name using String.Format
            string resourceName = String.Format("{0}.{1}", namespacePrefix, imageName);

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    return Image.FromStream(stream);
                }
                else
                {
                    // Optionally, you could log an error or throw an exception here
                    throw new ArgumentException(String.Format("Resource '{0}' not found in assembly."), resourceName);
                }
            }
        }
    }
}
