package androidx.annotation;

import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;

/** Compile-only stand-in (androidx.annotation is on Google Maven only). */
@Retention(RetentionPolicy.CLASS)
public @interface RestrictTo {
  Scope[] value();

  enum Scope { LIBRARY, LIBRARY_GROUP, LIBRARY_GROUP_PREFIX, GROUP_ID, TESTS, SUBCLASSES }
}
