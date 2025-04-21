const hamburgerBtn = document.getElementById('hamburger-btn');
const sideNav = document.getElementById('side-nav');
const overlay = document.getElementById('overlay');
const mainContent = document.getElementById('main-content'); // If pushing content
const body = document.body;

const navLangBtnEn = document.getElementById('nav-btn-en');
const navLangBtnDe = document.getElementById('nav-btn-de');
const tocEnContainer = document.getElementById('toc-en');
const tocDeContainer = document.getElementById('toc-de');

// --- Toggle Side Navigation ---
function toggleNav() {
    const isOpen = sideNav.classList.toggle('open');
    overlay.classList.toggle('open');
    hamburgerBtn.classList.toggle('open');
    hamburgerBtn.setAttribute('aria-expanded', isOpen);
    sideNav.setAttribute('aria-hidden', !isOpen);
    // Optional: Add class to body to prevent scrolling or push content
    // body.classList.toggle('nav-open');
}

// --- Event Listeners ---
hamburgerBtn.addEventListener('click', toggleNav);
overlay.addEventListener('click', toggleNav); // Close nav if overlay is clicked

// --- Language Switching ---
function switchLanguage(lang) {
    // Hide all main language content divs
    const contents = mainContent.querySelectorAll('.lang-content');
    contents.forEach(content => {
        content.style.display = 'none';
    });

    // Show the selected main language content div
    const selectedContent = mainContent.querySelector('.lang-' + lang);
    if (selectedContent) {
        selectedContent.style.display = 'block';
    }

    // Update language buttons in nav
    if (lang === 'en') {
        navLangBtnEn.classList.add('active');
        navLangBtnDe.classList.remove('active');
        tocEnContainer.style.display = 'block'; // Show EN TOC
        tocDeContainer.style.display = 'none';  // Hide DE TOC
    } else {
        navLangBtnEn.classList.remove('active');
        navLangBtnDe.classList.add('active');
        tocEnContainer.style.display = 'none';   // Hide EN TOC
        tocDeContainer.style.display = 'block'; // Show DE TOC
    }

    // Store the preference in localStorage
    try {
        localStorage.setItem('preferredLanguage', lang);
    } catch (e) {
        console.warn("Could not save language preference to localStorage:", e);
    }

    // Update the document's lang attribute
    document.documentElement.lang = lang;

    // Close nav after language switch (optional, but often desired on mobile)
    if (sideNav.classList.contains('open')) {
        // Only close if it was open, avoid toggling if already closed
        // Use setTimeout to allow visual feedback on button before closing
        setTimeout(toggleNav, 150);
    }
}

// --- TOC Link Handling ---
sideNav.querySelectorAll('.nav-toc a[href^="#"]').forEach(anchor => {
    anchor.addEventListener('click', function (e) {
        e.preventDefault();
        // Close the navigation panel *before* scrolling
        if (sideNav.classList.contains('open')) {
            toggleNav();
        }

        const targetId = this.getAttribute('href');
        // Use setTimeout to allow the nav to finish closing before scrolling
        setTimeout(() => {
            const targetElement = document.getElementById(targetId.substring(1));
            if (targetElement) {
                // Use native scrollIntoView
                targetElement.scrollIntoView({
                    behavior: 'smooth',
                    block: 'start'
                });
                // Update hash in URL manually *after* scrolling (optional)
                // history.pushState(null, null, targetId);
            } else {
                console.warn("Target element for TOC link not found:", targetId);
            }
        }, 350); // Adjust delay slightly longer than transition
    });
});


// --- Initial Setup On Page Load ---
document.addEventListener('DOMContentLoaded', () => {
    let preferredLang = 'en'; // Default to English
    try {
        const savedLang = localStorage.getItem('preferredLanguage');
        if (savedLang && (savedLang === 'en' || savedLang === 'de')) {
            preferredLang = savedLang;
        }
    } catch (e) {
        console.warn("Could not read language preference from localStorage:", e);
    }
    switchLanguage(preferredLang); // Set initial language and TOC visibility
});