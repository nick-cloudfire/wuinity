//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace PREACT.Input
{
    /// <summary>
    /// Marks a public member of an input class that is not part of the <c>.wui</c> format, so
    /// <see cref="PREACTInputWriter"/> does not write it. For members kept only because other code still
    /// binds to them (a retired key the GUI shows), and for runtime state that happens to be public.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field | System.AttributeTargets.Property)]
    public sealed class NotInFileAttribute : System.Attribute
    {
    }
}
