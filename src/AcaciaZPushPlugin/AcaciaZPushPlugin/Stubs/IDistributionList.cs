﻿/// Copyright 2016 Kopano b.v.
/// 
/// This program is free software: you can redistribute it and/or modify
/// it under the terms of the GNU Affero General Public License, version 3,
/// as published by the Free Software Foundation.
/// 
/// This program is distributed in the hope that it will be useful,
/// but WITHOUT ANY WARRANTY; without even the implied warranty of
/// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
/// GNU Affero General Public License for more details.
/// 
/// You should have received a copy of the GNU Affero General Public License
/// along with this program.If not, see<http://www.gnu.org/licenses/>.
/// 
/// Consult LICENSE file for details

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Acacia.Stubs
{
    public interface IDistributionList : IItem
    {
        string DLName { get; set; }
        string SMTPAddress { get; set; }

        /// <summary>
        /// Adds a member to the distribution list.
        /// </summary>
        /// <param name="item">The item. This is not disposed or released.</param>
        /// <exception cref="NotSupportedException">If the item is not a contact or distribution list</exception>
        void AddMember(IItem item);
    }
}
