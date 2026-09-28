using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using PREACT.Evacuation;
using PREACT.Math;

namespace PREACT.Input
{
    /// <summary>
    /// Writes a <see cref="PREACTInput"/> back out in the <c>.wui</c> format its parsers read.
    ///
    /// The reader is a set of hand-written per-category parsers that look their values up by
    /// <c>nameof(Field)</c>. Rather than mirror each of them by hand - which would be a second
    /// place to forget a field whenever one is added - the key/value body of each section is
    /// emitted by reflecting over the same public members those parsers name. Section structure
    /// (which headers exist, in what order, and which module sub-section belongs to which module)
    /// is explicit, because that part is not derivable from the types.
    ///
    /// Verified by round-tripping: a real case is loaded, written, reloaded, and the two parsed
    /// inputs compared field by field.
    /// </summary>
    public static class PREACTInputWriter
    {
        public static string[] Write(PREACTInput input)
        {
            return Write(input, true);
        }

        /// <param name="omitDefaultSections">Leave out a section that holds only what its absence would mean (a
        /// module that is off and has nothing configured below it, a map with the default provider). False writes
        /// every section - what the tests use to learn the defaults.</param>
        public static string[] Write(PREACTInput input, bool omitDefaultSections)
        {
            var lines = new List<string>();
            bool omit = omitDefaultSections;

            Section(lines, nameof(PREACTInput.Simulation), input.Simulation, omit);
            Section(lines, nameof(PREACTInput.Map), input.Map, omit);
            Section(lines, nameof(PREACTInput.Landscape), input.Landscape, omit);
            Section(lines, nameof(PREACTInput.Population), input.Population, omit);

            //Demographics belong to the population section and are read from their own repeated
            //headers, so they follow it.
            if (input.Population != null)
            {
                foreach (KeyValuePair<string, DemographicsInput> kv in input.Population.Demographics)
                {
                    Section(lines, "Demographics", kv.Value, omit);
                }
            }

            Section(lines, nameof(PREACTInput.Evacuation), input.Evacuation, omit);
            if (input.Evacuation != null)
            {
                foreach (KeyValuePair<string, ResponseCurve> kv in input.Evacuation.ResponseCurves)
                {
                    ResponseCurveSection(lines, kv.Value, input.Simulation != null ? input.Simulation.StartDateTime : default);
                }
                foreach (KeyValuePair<string, EvacuationDestinationInput> kv in input.Evacuation.EvacuationDestinationInputs)
                {
                    Section(lines, "Destination", kv.Value, omit);
                }
                foreach (KeyValuePair<string, EvacuationGroupInput> kv in input.Evacuation.EvacuationGroupInputs)
                {
                    Section(lines, "EvacuationGroup", kv.Value, omit);
                }
            }

            Section(lines, nameof(PREACTInput.Weather), input.Weather, omit);

            //Each module is followed by the sub-sections named after its module options, which is how the parsers
            //locate them (headerLineIndex[nameof(TrafficModules.SUMO)] and so on). The selected one is always
            //written; the others when they hold anything, so switching the fire from ELMFIRE to AscImport and back,
            //or smoke off to None, loses no setting. They used to be dropped, and with them the ELMFIRE settings the
            //case build reads whichever module is selected. The module section is only ever left out when nothing
            //follows it, since its sub-sections are read through it.
            var sub = new List<string>();
            ModuleSubSections(sub, input.PedestrianModule, omit);
            ModuleWithSubSections(lines, nameof(PREACTInput.PedestrianModule), input.PedestrianModule, sub, omit);

            sub = new List<string>();
            ModuleSubSections(sub, input.TrafficModule, omit);
            ModuleWithSubSections(lines, nameof(PREACTInput.TrafficModule), input.TrafficModule, sub, omit);

            sub = new List<string>();
            ModuleSubSections(sub, input.WildfireModule, omit);

            //The ELMFIRE namelist settings, which Section cannot reach: they hang off ElmfireInput as a nested object
            //and IsWritable refuses those, deliberately - the format has no nesting. Written with the ELMFIRE module,
            //or whenever they are not the defaults.
            ElmfireNamelistInput namelist = input.WildfireModule?.ElmfireInput?.Namelist;
            if (namelist != null && (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE
                                     || !omit || !IsDefault(namelist)))
            {
                Section(sub, ElmfireInput.NamelistSection, namelist, omit);
            }

            //Ignition points live on WildfireData, which Section skips along with every other "Data"
            //member, so they are written here explicitly - one repeated section each, the same shape
            //destinations and evacuation groups use. Read through [WildfireModule], so they keep it written.
            if (input.WildfireModule?.Data?.IgnitionPoints != null)
            {
                foreach (Wildfire.IgnitionPointInput point in input.WildfireModule.Data.IgnitionPoints)
                {
                    IgnitionPointSection(sub, point);
                }
            }
            ModuleWithSubSections(lines, nameof(PREACTInput.WildfireModule), input.WildfireModule, sub, omit);

            sub = new List<string>();
            ModuleSubSections(sub, input.SmokeModule, omit);
            ModuleWithSubSections(lines, nameof(PREACTInput.SmokeModule), input.SmokeModule, sub, omit);

            sub = new List<string>();
            ModuleSubSections(sub, input.TriggerBufferModule, omit);
            ModuleWithSubSections(lines, nameof(PREACTInput.TriggerBufferModule), input.TriggerBufferModule, sub, omit);

            return lines.ToArray();
        }

        private static void ModuleWithSubSections(List<string> lines, string header, object module, List<string> subSections, bool omit)
        {
            Section(lines, header, module, omit && subSections.Count == 0);
            lines.AddRange(subSections);
        }

        /// <summary>
        /// The sub-sections of one module, one per option of its <c>Module</c> enum (<c>None</c> has none): the
        /// selected option's always, the others' when they are not what a fresh instance holds (every one of them
        /// when <paramref name="omit"/> is false). Found by name, so a module gaining an option writes its section
        /// without a change here: the accessor is looked up from the enum value (<c>ELMFIRE</c> -&gt;
        /// <c>ElmfireInput</c>).
        /// </summary>
        private static void ModuleSubSections(List<string> lines, object module, bool omit)
        {
            if (module == null) return;

            object selected = ReadMember(module, "Module");
            if (selected == null || !selected.GetType().IsEnum) return;

            foreach (object option in Enum.GetValues(selected.GetType()))
            {
                string name = option.ToString();
                if (name == "None") continue;

                object subInput = FindSubInput(module, name);
                if (subInput == null) continue;

                if (option.Equals(selected) || !omit || !IsDefault(subInput))
                {
                    Section(lines, name, subInput, omit);
                }
            }
        }

        /// <summary>
        /// Finds the property on a module holding the sub-input for the selected module, matching
        /// on the module name (e.g. AscImport -> AscImportInput).
        /// </summary>
        private static object FindSubInput(object module, string moduleName)
        {
            Type t = module.GetType();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                if (!p.Name.Equals(moduleName + "Input", StringComparison.OrdinalIgnoreCase) &&
                    !p.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) continue;

                try { return p.GetValue(module); } catch { return null; }
            }
            return null;
        }

        private static object ReadMember(object target, string name)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(target);

            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead) { try { return p.GetValue(target); } catch { return null; } }

            return null;
        }

        /// <summary>
        /// A response curve is the one section that is not purely key/value: its data points are
        /// written as bare "time,probability" rows after the header keys, which is the shape the
        /// parser expects.
        /// </summary>
        /// <remarks>
        /// An Absolute curve is held as seconds after the simulation start and written back as dates, so it
        /// reloads as the same curve. It used to be written as those seconds under <c>TimeInput=Absolute</c>,
        /// which the next load could not read, losing the curve.
        /// </remarks>
        private static void ResponseCurveSection(List<string> lines, ResponseCurve curve, DateTime simulationStart)
        {
            lines.Add("[ResponseCurve]");
            lines.Add("Name=" + curve.Name);
            lines.Add("TimeInput=" + curve.TimeInput);

            if (curve.DataPoints != null)
            {
                for (int i = 0; i < curve.DataPoints.Length; ++i)
                {
                    ResponseDataPoint p = curve.DataPoints[i];
                    string time = curve.TimeInput == TimeInputs.Absolute
                        ? simulationStart.AddSeconds(System.Math.Round((double)p.Time)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                        : F(p.Time);
                    lines.Add(time + "," + F(p.Probability));
                }
            }
            lines.Add(string.Empty);
        }

        /// <summary>
        /// One ignition point. Written by hand rather than reflected over, because only two of the four
        /// fields are input: <c>IgnitionTime</c> and <c>IgnitionDateTime</c> are each derived from the
        /// other, and writing both would let a file disagree with itself about when the fire starts.
        /// </summary>
        private static void IgnitionPointSection(List<string> lines, Wildfire.IgnitionPointInput point)
        {
            lines.Add("[IgnitionPoint]");
            lines.Add("LatLon=" + F(point.LatLon.x) + "," + F(point.LatLon.y));
            lines.Add("AbsoluteTime=" + (point.AbsoluteTime ? "true" : "false"));
            if (point.AbsoluteTime)
            {
                lines.Add("IgnitionDateTime=" + point.IgnitionDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            }
            else
            {
                lines.Add("IgnitionTime=" + F(point.IgnitionTime));
            }
            lines.Add(string.Empty);
        }

        private static void Section(List<string> lines, string header, object source, bool omitDefault)
        {
            if (source == null) return;

            var body = new List<string>();
            Type t = source.GetType();

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Skip(f.Name, f.FieldType) || f.IsDefined(typeof(NotInFileAttribute), true)) continue;
                Emit(body, f.Name, f.GetValue(source));
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (Skip(p.Name, p.PropertyType) || p.IsDefined(typeof(NotInFileAttribute), true)) continue;
                //A property without a setter is computed from something else (HasWeatherAnchor, say): writing it
                //adds a key no parser reads.
                if (!p.CanWrite) continue;

                object value;
                try { value = p.GetValue(source); } catch { continue; }
                Emit(body, p.Name, value);
            }

            //A section that says only what its absence would say is omitted too, for the sections where
            //absent and default-valued mean the same thing (a disabled module, a map with the default
            //provider). Writing them turned a file without, say, a [WildfireModule] into one with an empty
            //module - which then reported "nothing has been painted" on the next load.
            if (omitDefault && OmittedWhenDefault.Contains(header) && IsDefault(source, body))
            {
                return;
            }

            //A section with nothing to say is omitted entirely. The parsers treat a present header
            //as a declaration that the feature is configured and then demand its required keys -
            //an empty [Weather], for instance, fails the load outright with "WeatherFile was not
            //found", where no [Weather] at all simply means no weather. Writing the header
            //unconditionally therefore produced files that could not be read back.
            if (body.Count == 0)
            {
                return;
            }

            lines.Add("[" + header + "]");
            lines.AddRange(body);
            lines.Add(string.Empty);
        }

        private static readonly HashSet<string> OmittedWhenDefault = new HashSet<string>
        {
            nameof(PREACTInput.Map), nameof(PREACTInput.Weather),
            nameof(PREACTInput.PedestrianModule), nameof(PREACTInput.TrafficModule), nameof(PREACTInput.WildfireModule),
            nameof(PREACTInput.SmokeModule), nameof(PREACTInput.TriggerBufferModule),
        };

        /// <summary>Whether <paramref name="source"/> writes what a freshly constructed instance of its type writes.</summary>
        private static bool IsDefault(object source)
        {
            var body = new List<string>();
            Section(body, "probe", source, false);
            if (body.Count > 0)
            {
                body.RemoveAt(0);
                body.RemoveAt(body.Count - 1);
            }
            return IsDefault(source, body);
        }

        /// <summary>Whether <paramref name="body"/> is what a freshly constructed instance of the same type writes.</summary>
        private static bool IsDefault(object source, List<string> body)
        {
            object fresh;
            try
            {
                fresh = Activator.CreateInstance(source.GetType());
            }
            catch (Exception)
            {
                return false;
            }

            var freshLines = new List<string>();
            Section(freshLines, "probe", fresh, false);
            //Section adds the header and a trailing blank line around the body.
            if (freshLines.Count == 0)
            {
                return body.Count == 0;
            }
            freshLines.RemoveAt(0);
            freshLines.RemoveAt(freshLines.Count - 1);
            return freshLines.SequenceEqual(body);
        }

        /// <summary>
        /// The keys a section of type <paramref name="t"/> is written with: the same members <see cref="Section"/>
        /// writes, whatever their values.
        /// </summary>
        /// <remarks>
        /// The reader's list of known keys (<see cref="PREACTInput"/> reports every other key as ignored), so that
        /// what can be read and what is written are one list. The parsers look their keys up by
        /// <c>nameof(member)</c> of the same members.
        /// </remarks>
        internal static IEnumerable<string> FileKeys(Type t)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Skip(f.Name, f.FieldType) || f.IsDefined(typeof(NotInFileAttribute), true)) continue;
                yield return f.Name;
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (Skip(p.Name, p.PropertyType) || p.IsDefined(typeof(NotInFileAttribute), true)) continue;
                yield return p.Name;
            }
        }

        /// <summary>
        /// Members that are not part of the file: derived runtime state, the root folder (implied
        /// by where the file is), and anything not expressible as a single value.
        /// </summary>
        private static bool Skip(string name, Type type)
        {
            if (name == "Data" || name == "RootFolder") return true;
            return !IsWritable(type);
        }

        private static bool IsWritable(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)) return true;
            if (t.Name == "Vector2d" || t.Name == "Vector2int" || t.Name == "Vector2") return true;

            //r,g,b, the three components the parser splits on
            if (t.Name == "PREACTColor") return true;

            //arrays and lists of simple values are written comma-separated, matching how the
            //parsers read Destinations, DestinationsCDF, ResponseCurves and the rest
            if (t.IsArray) return IsWritable(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                return IsWritable(t.GetGenericArguments()[0]);
            }

            return false;
        }

        private static void Emit(List<string> body, string name, object value)
        {
            if (value == null) return;

            //An unset date is not a value: WeatherAnchorDateTime=0001-01-01T00:00:00 used to be written for
            //every scenario without an anchor.
            if (value is DateTime date && date == default) return;

            string text = Format(value);
            if (text == null) return;

            //Paths are stored with forward slashes, which every platform reads; a backslash is a separator on
            //Windows only. Applies to the scenario's own path keys (PascalCase ...File/...Directory/...Folder/
            //...Path), not to ELMFIRE's namelist values, which are ELMFIRE's to interpret.
            if (value is string && IsPathKey(name))
            {
                text = text.Replace('\\', '/');
            }

            //An unset path written as "Key=" is worse than omitting it: the parsers find the key,
            //try to resolve it as a file and fail the load, whereas an absent optional key is
            //handled gracefully.
            if (text.Length == 0) return;

            body.Add(name + "=" + text);
        }

        private static bool IsPathKey(string name)
        {
            return name.EndsWith("File", StringComparison.Ordinal) || name.EndsWith("Directory", StringComparison.Ordinal)
                   || name.EndsWith("Folder", StringComparison.Ordinal) || name.EndsWith("Path", StringComparison.Ordinal);
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case string s: return s;
                case DateTime d: return d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                case double dd: return F(dd);
                case float ff: return F(ff);
                case Vector2d v: return F(v.x) + "," + F(v.y);
                //System.Numerics.Vector2, used for pairs such as WalkingSpeedMinMax
                case System.Numerics.Vector2 v2: return F(v2.X) + "," + F(v2.Y);
                //Vector2int needs stating explicitly even though it is IFormattable, because its
                //own ToString yields "(1, 2)" - parentheses and a space - which none of the
                //parsers read. Without this case it reached the IFormattable fallback below and
                //quietly wrote a value that could not be loaded back.
                case Vector2int vi: return vi.x.ToString(CultureInfo.InvariantCulture) + "," + vi.y.ToString(CultureInfo.InvariantCulture);
                //Three components, which is exactly what the parsers split on; alpha is not part
                //of the format. Kept ahead of the IFormattable fallback so this stays correct if
                //PREACTColor ever gains a ToString of its own.
                case PREACTColor colour: return F(colour.r) + "," + F(colour.g) + "," + F(colour.b);
            }

            if (value is Enum) return value.ToString();
            if (value is IFormattable formattable && !(value is IEnumerable))
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            //Arrays and lists share the comma-separated form. Handled after the scalar cases so a
            //string, which is itself enumerable, never reaches here.
            if (value is IEnumerable sequence)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) sb.Append(',');
                    sb.Append(Format(item));
                    first = false;
                }
                return sb.ToString();
            }

            return null;
        }

        /// <summary>Round-trippable and culture-independent, so a comma-decimal locale cannot turn
        /// one value into two.</summary>
        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
