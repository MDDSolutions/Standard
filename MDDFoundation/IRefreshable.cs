using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public interface IRefreshable
    {
        Task RefreshContentsAsync();
        void RefreshContents();

    }
}
