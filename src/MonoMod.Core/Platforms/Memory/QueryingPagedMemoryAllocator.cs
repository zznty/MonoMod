using MonoMod.Utils;
using System;
using System.Diagnostics.CodeAnalysis;

namespace MonoMod.Core.Platforms.Memory
{
    /// <summary>
    /// A base type providing OS methods required for <see cref="QueryingPagedMemoryAllocator"/>.
    /// </summary>
    public abstract class QueryingMemoryPageAllocatorBase
    {
        /// <summary>
        /// Gets the page size.
        /// </summary>
        public abstract uint PageSize { get; }
        /// <summary>
        /// Tries to query the specified page for information.
        /// </summary>
        /// <param name="pageAddr">The address of the page to query.</param>
        /// <param name="isFree"><see langword="true"/> if the page is free; <see langword="false"/> if it is allocated.</param>
        /// <param name="allocBase">The base of the page allocation this page is a part of.</param>
        /// <param name="allocSize">The size of the page allocation this page is a part of.</param>
        /// <returns><see langword="true"/> if the page was successfully queried; <see langword="false"/> otherwise.</returns>
        public abstract bool TryQueryPage(IntPtr pageAddr, out bool isFree, out IntPtr allocBase, out nint allocSize);
        /// <summary>
        /// Tries to allocate a page.
        /// </summary>
        /// <param name="size">The size of the page allocation.</param>
        /// <param name="executable"><see langword="true"/> if the page should be executable; <see langword="false"/> otherwise.</param>
        /// <param name="allocated">The address of the allocated page, if successful.</param>
        /// <returns><see langword="true"/> if a page was successfully allocated; <see langword="false"/> otherwise.</returns>
        public abstract bool TryAllocatePage(nint size, bool executable, out IntPtr allocated);
        /// <summary>
        /// Tries to allocate a specific page.
        /// </summary>
        /// <param name="pageAddr">The address of the page to allocate.</param>
        /// <param name="size">The size of the page allocation.</param>
        /// <param name="executable"><see langword="true"/> if the page should be executable; <see langword="false"/> otherwise.</param>
        /// <param name="allocated">The address of the allocated page, if successful.</param>
        /// <returns><see langword="true"/> if a page was successfully allocated; <see langword="false"/> otherwise.</returns>
        public abstract bool TryAllocatePage(IntPtr pageAddr, nint size, bool executable, out IntPtr allocated);
        /// <summary>
        /// Tries to free the page at the provided addresss.
        /// </summary>
        /// <param name="pageAddr">The address of the page to free.</param>
        /// <param name="errorMsg">An error message describing the error that ocurred, if any.</param>
        /// <returns><see langword="true"/> if the page was successfully freed; <see langword="false"/> otherwise.</returns>
        public abstract bool TryFreePage(IntPtr pageAddr, [NotNullWhen(false)] out string? errorMsg);

        /// <summary>
        /// Gets whether <see cref="TryFreePage"/> can release a single page which was mapped as part of a larger
        /// block, leaving the rest of the block mapped.
        /// </summary>
        /// <remarks>
        /// Block mappings amortize range searches over many pages, but they are only safe when the pages can be
        /// released individually - <c>VirtualFree(ptr, 0, MEM_RELEASE)</c> drops an entire reservation at once,
        /// so on Windows blocks must stay single-page.
        /// </remarks>
        public virtual bool SupportsPartialFree => false;
    }

    /// <summary>
    /// A <see cref="PagedMemoryAllocator"/> built around querying pages in memory.
    /// </summary>
    public sealed class QueryingPagedMemoryAllocator : PagedMemoryAllocator
    {
        private readonly QueryingMemoryPageAllocatorBase pageAlloc;

        /// <summary>
        /// The page a range allocation last succeeded from, used as the starting point of the next range search.
        /// </summary>
        /// <remarks>
        /// The request only requires an address inside its bounds - closeness to <see cref="PositionedAllocationRequest.Target"/>
        /// is a preference. Restarting every search at the target makes the search cost grow with the number of
        /// mappings between the target and the free space, which is quadratic over a fragmented address space
        /// (thousands of small mappings inside a +-2GB window cost minutes per allocation). Resuming near the
        /// previous result keeps the common case at a handful of probes.
        /// </remarks>
        private nint nextSearchHint;

        /// <summary>
        /// Maximum number of pages mapped in a single range search. Bounds both the address space a single
        /// search can claim and the memory wasted when a request only needs a fraction of the block.
        /// </summary>
        private const int MaxBlockPages = 64;
        /// <summary>
        /// Constructs a <see cref="QueryingPagedMemoryAllocator"/> using the provided <see cref="QueryingMemoryPageAllocatorBase"/>.
        /// </summary>
        /// <param name="alloc">The page allocator to use.</param>
        public QueryingPagedMemoryAllocator(QueryingMemoryPageAllocatorBase alloc)
            : base((nint)Helpers.ThrowIfNull(alloc).PageSize)
        {
            pageAlloc = alloc;
        }

        /// <inheritdoc/>
        protected override bool TryAllocateNewPage(AllocationRequest request, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            if (!pageAlloc.TryAllocatePage(PageSize, request.Executable, out var allocBase))
            {
                allocated = null;
                return false;
            }

            var pageObj = new Page(this, allocBase, (uint)PageSize, request.Executable);
            InsertAllocatedPage(pageObj);

            // now that we have a page, we'll try to allocate out of it
            // if that fails, immediately register for cleanup
            if (!pageObj.TryAllocate((uint)request.Size, (uint)request.Alignment, out var alloc))
            {
                RegisterForCleanup(pageObj);
                allocated = null;
                return false;
            }

            // we successfully allocated, return the page allocation
            allocated = alloc;
            return true;
        }

        /// <inheritdoc/>
        protected override bool TryAllocateNewPage(PositionedAllocationRequest request, nint targetPage, nint lowPageBound, nint highPageBound, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            // we'll do the same approach for trying to find an existing page, but querying the OS for free pages to allocate
            var target = request.Target;

            // Resume near where the last range allocation succeeded when that is inside the requested bounds;
            // otherwise start at the target.
            var startPage = nextSearchHint != 0 && nextSearchHint >= lowPageBound && nextSearchHint < highPageBound
                ? nextSearchHint
                : targetPage;

            var lowPage = startPage;
            var highPage = startPage + PageSize;

            while (lowPage >= lowPageBound || highPage < highPageBound)
            {
                // first check the high pages, while they're closer than low pages
                while (
                    highPage < highPageBound &&
                    (lowPage < lowPageBound || target - lowPage > highPage - target)
                )
                {
                    if (TryAllocNewPage(request, ref highPage, true, out allocated))
                        return true;
                }

                // then try low pages, while they're closer than high pages
                while (
                    lowPage >= lowPageBound &&
                    (highPage >= highPageBound || target - lowPage < highPage - target)
                )
                {
                    if (TryAllocNewPage(request, ref lowPage, false, out allocated))
                        return true;
                }
            }

            // if we fall out to here, we just couldn't allocate, so sucks
            allocated = null;
            return false;
        }

        private unsafe bool TryAllocNewPage(PositionedAllocationRequest request, ref nint page, bool goingUp, [MaybeNullWhen(false)] out IAllocatedMemory allocated)
        {
            if (pageAlloc.TryQueryPage(page, out var isFree, out var baseAddr, out var allocSize))
            {
                if (!isFree) // this is not a free block, so we don't care
                    goto Fail;

                // Map a block rather than a single page. Range searches are expensive (one or two OS calls per
                // probe over a fragmented address space), and every page registered here is served from
                // AllocList afterwards - without touching the OS at all - so mapping a block amortizes the
                // search over all the allocations that follow it in this window.
                var blockPages = pageAlloc.SupportsPartialFree ? (int)(allocSize / PageSize) : 1;
                if (blockPages < 1)
                    blockPages = 1;
                else if (blockPages > MaxBlockPages)
                    blockPages = MaxBlockPages;

                var blockSize = (nint)blockPages * PageSize;
                if (!pageAlloc.TryAllocatePage(page, blockSize, request.Base.Executable, out var allocBase)) // allocation failed
                    goto Fail;

                Page? blockFirstPage = null;
                for (var blockIndex = 0; blockIndex < blockPages; blockIndex++)
                {
                    var blockPage = new Page(this, allocBase + (nint)blockIndex * PageSize, (uint)PageSize, request.Base.Executable);
                    InsertAllocatedPage(blockPage);
                    blockFirstPage ??= blockPage;
                }

                var pageObj = blockFirstPage!;

                // now that we have a page, we'll try to allocate out of it
                // if that fails, immediately register for cleanup
                if (!pageObj.TryAllocate((uint)request.Base.Size, (uint)request.Base.Alignment, out var alloc))
                {
                    RegisterForCleanup(pageObj);
                    goto Fail;
                }

                if ((nint)alloc.BaseAddress < request.LowBound || (nint)alloc.BaseAddress >= request.HighBound)
                {
                    MMDbgLog.Error($"Got allocation request at {request.Target:X} within range [{request.LowBound:X}, {request.HighBound:X}) but received out-of-bounds allocation {alloc.BaseAddress:X} within page {pageObj.BaseAddr:X} (size: {pageObj.Size}). TryQueryPage gave baseAddr: {baseAddr:X}");
                    alloc.Dispose();
                    RegisterForCleanup(pageObj);
                    goto Fail;
                }

                // we successfully allocated, return the page allocation
                nextSearchHint = pageObj.BaseAddr;
                allocated = alloc;
                return true;

                Fail:
                // We're failing out, update the page address appropriately
                if (goingUp)
                    page = baseAddr + allocSize;
                else
                    page = baseAddr - PageSize;

                allocated = null;
                return false;
            }
            else
            {
                // TODO: check GetLastError

                // query failed, fail out
                if (goingUp)
                    page += PageSize;
                else
                    page -= PageSize;
                allocated = null;
                return false;
            }
        }

        /// <inheritdoc/>
        protected override bool TryFreePage(Page page, [NotNullWhen(false)] out string? errorMsg)
            => pageAlloc.TryFreePage(page.BaseAddr, out errorMsg);
    }
}
