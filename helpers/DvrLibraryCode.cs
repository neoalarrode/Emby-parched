using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace EmbyNoShare
{
    /// <summary>Fase B del DVR (BORRADOR, aún no activo): carpeta y biblioteca por usuario, automáticas.
    ///
    /// Todo por reflexión, sin tipos de Emby, para copiarlo dentro de Emby.LiveTV.dll y que valga entre versiones.
    /// La idea: la primera vez que un usuario graba, bajo la carpeta raíz de grabaciones del DVR se crea
    /// «&lt;raíz&gt;/&lt;usuario&gt;», se da de alta como biblioteca si no existe, y se ajustan los permisos para que ese
    /// usuario vea su biblioteca de grabaciones y NO las de los demás, conservando el acceso a sus bibliotecas normales.
    ///
    /// PENDIENTE DE VALIDAR EN VIVO (Emby de pruebas), por eso no está activado:
    ///  - Crear la biblioteca con ILibraryManager.AddVirtualFolder(name, collectionType, LibraryOptions, refresh) y
    ///    AddMediaPaths(folder, MediaPathInfo[]{Path=&lt;raíz&gt;/&lt;usuario&gt;}, refresh). Falta el collectionType correcto
    ///    de grabaciones y comprobar que no dispara un escaneo pesado cada vez.
    ///  - Permisos: UserPolicy.EnableAllFolders=false + EnabledFolders con las bibliotecas normales + la suya. Es el
    ///    punto delicado: hay que NO quitarle el acceso a pelis/series. Se afina contra el Emby de pruebas.
    ///  - De dónde sale la carpeta raíz: LiveTvOptions (RecordingPath / la biblioteca de grabaciones configurada).
    /// </summary>
    public static class DvrLibraryCode
    {
        /// <summary>Nombre de carpeta válido a partir del nombre de usuario (quita lo que no vale en una ruta).</summary>
        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "_";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++)
                    if (bad[i] == c) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "_" : s;
        }

        /// <summary>Crea «&lt;root&gt;/&lt;usuario&gt;» en disco si no existe y la devuelve. null si no se puede.</summary>
        public static string EnsureUserFolder(string root, string username)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(username))
                    return null;
                string dir = Path.Combine(root, SafeName(username));
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Inserta la subcarpeta del usuario en una ruta ya formada: si 'fullPath' cuelga de 'root', devuelve
        /// «root/&lt;usuario&gt;/resto». Si no (otra raíz, p. ej. películas) o falta algo, devuelve 'fullPath' sin tocar.
        /// Así la grabación cae en la carpeta del usuario sin romper nada si algo no encaja.</summary>
        public static string UserRecordingPath(string fullPath, string root, string username)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(root) || string.IsNullOrEmpty(username))
                    return fullPath;
                string r = root.TrimEnd('/', '\\');
                if (!fullPath.StartsWith(r, StringComparison.Ordinal))
                    return fullPath;
                string rest = fullPath.Substring(r.Length).TrimStart('/', '\\');
                return Path.Combine(r, SafeName(username), rest);
            }
            catch (Exception)
            {
                return fullPath;
            }
        }

        // --- A completar/validar contra el Emby de pruebas: creación de biblioteca y permisos ---
        // static object EnsureLibrary(object libraryManager, string dirPath, string displayName) { ... AddVirtualFolder + AddMediaPaths ... }
        // static void GrantOnlyOwn(object userManager, object user, string ownLibraryId) { ... EnabledFolders ... }
    }
}
